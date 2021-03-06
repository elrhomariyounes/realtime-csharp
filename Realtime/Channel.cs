﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Timers;
using Newtonsoft.Json;
using Supabase.Realtime.Attributes;
using WebSocketSharp;
using static Supabase.Realtime.Channel;

namespace Supabase.Realtime
{
    /// <summary>
    /// Class representation of a channel subscription
    /// </summary>
    public class Channel
    {
        /// <summary>
        /// Channel state with associated string representations.
        /// </summary>
        public enum ChannelState
        {
            [MapTo("closed")]
            Closed,
            [MapTo("errored")]
            Errored,
            [MapTo("joined")]
            Joined,
            [MapTo("joining")]
            Joining,
            [MapTo("leaving")]
            Leaving
        }

        /// <summary>
        /// Invoked when the `INSERT` event is raised.
        /// </summary>
        public event EventHandler<SocketResponseEventArgs> OnInsert;

        /// <summary>
        /// Invoked when the `UPDATE` event is raised.
        /// </summary>
        public event EventHandler<SocketResponseEventArgs> OnUpdate;

        /// <summary>
        /// Invoked when the `DELETE` event is raised.
        /// </summary>
        public event EventHandler<SocketResponseEventArgs> OnDelete;

        /// <summary>
        /// Invoked anytime a message is decoded within this topic.
        /// </summary>
        public event EventHandler<SocketResponseEventArgs> OnMessage;

        /// <summary>
        /// Invoked when this channel listener is closed
        /// </summary>
        public event EventHandler<ChannelStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Invoked when the socket drops or crashes.
        /// </summary>
        public event EventHandler<ChannelStateChangedEventArgs> OnError;

        /// <summary>
        /// Invoked when the channel is explicitly closed by the client.
        /// </summary>
        public event EventHandler<ChannelStateChangedEventArgs> OnClose;

        public bool IsClosed => State == ChannelState.Closed;
        public bool IsErrored => State == ChannelState.Errored;
        public bool IsJoined => State == ChannelState.Joined;
        public bool IsJoining => State == ChannelState.Joining;
        public bool IsLeaving => State == ChannelState.Leaving;

        /// <summary>
        /// Shorthand accessor for the Client's socket connection.
        /// </summary>
        public Socket Socket { get => Client.Instance.Socket; }

        /// <summary>
        /// The Channel's current state.
        /// </summary>
        public ChannelState State { get; private set; } = ChannelState.Closed;

        /// <summary>
        /// The Channel's (unique) topic indentifier.
        /// </summary>
        public string Topic { get => Utils.GenerateChannelTopic(database, schema, table, col, value); }

        private string database;
        private string schema;
        private string table;
        private string col;
        private string value;

        private Push joinPush;
        private bool canPush => IsJoined && Socket.IsConnected;
        private bool hasJoinedOnce = false;
        private List<Push> buffer = new List<Push>();
        private Timer rejoinTimer;
        private bool isRejoining = false;

        /// <summary>
        /// Initializes a Channel - must call `Subscribe()` to receive events.
        /// </summary>
        /// <param name="database"></param>
        /// <param name="schema"></param>
        /// <param name="table"></param>
        /// <param name="col"></param>
        /// <param name="value"></param>
        public Channel(string database, string schema, string table, string col, string value)
        {
            this.database = database;
            this.schema = schema;
            this.table = table;

            this.col = col;
            this.value = value;

            joinPush = new Push(this, Constants.CHANNEL_EVENT_JOIN, null);

            rejoinTimer = new Timer(Client.Instance.Options.Timeout.TotalMilliseconds);
            rejoinTimer.Elapsed += HandleRejoinTimerElapsed;
            rejoinTimer.AutoReset = true;
        }

        /// <summary>
        /// Subscribes to the channel given supplied options/params.
        /// </summary>
        /// <param name="timeoutMs"></param>
        public Task<Channel> Subscribe(int timeoutMs = Constants.DEFAULT_TIMEOUT)
        {
            var tsc = new TaskCompletionSource<Channel>();

            if (hasJoinedOnce)
            {
                tsc.SetException(new Exception("`Subscribe` can only be called a single time per channel instance."));
                return tsc.Task;
            }

            EventHandler<ChannelStateChangedEventArgs> channelCallback = null;
            EventHandler joinPushTimeoutCallback = null;

            channelCallback = (object sender, ChannelStateChangedEventArgs e) =>
            {
                switch (e.State)
                {
                    // Success!
                    case ChannelState.Joined:
                        StateChanged -= channelCallback;
                        joinPush.OnTimeout -= joinPushTimeoutCallback;

                        // Clear buffer
                        foreach (var item in buffer)
                            item.Send();
                        buffer.Clear();

                        tsc.TrySetResult(this);
                        break;
                    // Failure
                    case ChannelState.Closed:
                    case ChannelState.Errored:
                        StateChanged -= channelCallback;
                        joinPush.OnTimeout -= joinPushTimeoutCallback;

                        tsc.TrySetException(new Exception("Error occurred connecting to channel. Check logs."));
                        break;
                }
            };

            // Throw an exception if there is a problem receiving a join response
            joinPushTimeoutCallback = (object sender, EventArgs e) =>
            {
                StateChanged -= channelCallback;
                joinPush.OnTimeout -= joinPushTimeoutCallback;

                tsc.TrySetException(new PushTimeoutException());
            };

            StateChanged += channelCallback;

            // Set a flag to prevent multiple join attempts.
            hasJoinedOnce = true;

            // Init and send join.
            Rejoin(timeoutMs);
            joinPush.OnTimeout += joinPushTimeoutCallback;

            return tsc.Task;
        }

        /// <summary>
        /// Unsubscribes from the channel.
        /// </summary>
        public void Unsubscribe()
        {
            SetState(ChannelState.Leaving);

            var leavePush = new Push(this, Constants.CHANNEL_EVENT_LEAVE, null);
            leavePush.Send();

            TriggerChannelStateEvent(new ChannelStateChangedEventArgs(ChannelState.Closed));
        }

        /// <summary>
        /// Sends a `Push` request under this channel.
        ///
        /// Maintains a buffer in the event push is called prior to the channel being joined.
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="payload"></param>
        /// <param name="timeoutMs"></param>
        public void Push(string eventName, object payload, int timeoutMs = Constants.DEFAULT_TIMEOUT)
        {
            if (!hasJoinedOnce)
                throw new Exception($"Tried to push '{eventName}' to '{Topic}' before joining. Use `Channel.Subscribe()` before pushing events");

            var pushEvent = new Push(this, eventName, payload, timeoutMs);

            if (canPush)
            {
                pushEvent.Send();
            }
            else
            {
                pushEvent.StartTimeout();
                buffer.Add(pushEvent);
            }
        }

        /// <summary>
        /// Rejoins the channel.
        /// </summary>
        /// <param name="timeoutMs"></param>
        public void Rejoin(int timeoutMs = Constants.DEFAULT_TIMEOUT)
        {
            if (IsLeaving) return;
            SendJoin(timeoutMs);
        }

        private void HandleRejoinTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (isRejoining) return;
            isRejoining = true;

            if (State != ChannelState.Closed && State != ChannelState.Errored)
                return;

            Client.Instance.Options.Logger?.Invoke(Topic, "attempting to rejoin", null);

            // Reset join push instance
            joinPush = new Push(this, Constants.CHANNEL_EVENT_JOIN, null);

            Rejoin();
        }

        private void SendJoin(int timeoutMs = Constants.DEFAULT_TIMEOUT)
        {
            SetState(ChannelState.Joining);

            // Remove handler if exists
            joinPush.OnMessage -= HandleJoinResponse;

            joinPush.OnMessage += HandleJoinResponse;
            joinPush.Resend(timeoutMs);
        }

        private void HandleJoinResponse(object sender, SocketResponseEventArgs args)
        {
            if (args.Response._event == Constants.CHANNEL_EVENT_REPLY)
            {
                var obj = JsonConvert.DeserializeObject<PheonixResponse>(JsonConvert.SerializeObject(args.Response.Payload, Client.Instance.SerializerSettings), Client.Instance.SerializerSettings);
                if (obj.Status == Constants.PHEONIX_STATUS_OK)
                {
                    SetState(ChannelState.Joined);

                    // Disable Rejoin Timeout
                    rejoinTimer?.Stop();
                    isRejoining = false;
                }
            }
        }

        private void SetState(ChannelState state)
        {
            State = state;
            StateChanged?.Invoke(this, new ChannelStateChangedEventArgs(state));
        }

        internal void HandleSocketMessage(SocketResponseEventArgs args)
        {
            if (args.Response.Ref == joinPush.Ref) return;

            // If we don't ignore this event we'll end up with double callbacks.
            if (args.Response._event == "*") return;

            OnMessage?.Invoke(this, args);

            switch (args.Response.Event)
            {
                case Constants.EventType.Insert:
                    OnInsert?.Invoke(this, args);
                    break;
                case Constants.EventType.Update:
                    OnUpdate?.Invoke(this, args);
                    break;
                case Constants.EventType.Delete:
                    OnDelete?.Invoke(this, args);
                    break;
            }
        }

        private void TriggerChannelStateEvent(ChannelStateChangedEventArgs args, bool shouldRejoin = true)
        {
            SetState(args.State);

            if (shouldRejoin)
            {
                isRejoining = false;
                rejoinTimer.Start();
            }
            else rejoinTimer.Stop();

            switch (args.State)
            {
                case ChannelState.Closed:
                    OnClose?.Invoke(this, args);
                    break;
                case ChannelState.Errored:
                    OnError?.Invoke(this, args);
                    break;
                default:
                    break;
            }
        }
    }

    public class ChannelStateChangedEventArgs : EventArgs
    {
        public ChannelState State { get; private set; }

        public ChannelStateChangedEventArgs(ChannelState state)
        {
            State = state;
        }
    }

    public class PheonixResponse
    {
        [JsonProperty("response")]
        public object Response;

        [JsonProperty("status")]
        public string Status;
    }
}
