/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections;
using System.Threading;
using Apache.NMS.Stomp.Commands;
using Apache.NMS.Stomp.Transport;
using Apache.NMS;
using Apache.NMS.Util;

namespace Apache.NMS.Stomp
{
    /// <summary>
    /// Represents a connection with a message broker
    /// </summary>
    public class Connection : IConnection
    {
        private readonly Uri brokerUri;
        private ITransport transport;
        private readonly ConnectionInfo info;
        private AcknowledgementMode acknowledgementMode = AcknowledgementMode.AutoAcknowledge;
        private TimeSpan requestTimeout;
        private readonly IList sessions = ArrayList.Synchronized(new ArrayList());
        private readonly IDictionary dispatchers = Hashtable.Synchronized(new Hashtable());
        private readonly object myLock = new object();
        private bool asyncSend = false;
        private bool alwaysSyncSend = false;
        private bool copyMessageOnSend = true;
        private bool connected = false;
        private bool closed = false;
        private bool closing = false;
        private int sessionCounter = 0;
        private int temporaryDestinationCounter = 0;
        private int localTransactionCounter;
        private readonly Atomic<bool> started = new Atomic<bool>(false);
        private ConnectionMetaData metaData = null;
        private bool disposed = false;
        private IRedeliveryPolicy redeliveryPolicy;
        private PrefetchPolicy prefetchPolicy = new PrefetchPolicy();

        public Connection(Uri connectionUri, ITransport transport, ConnectionInfo info)
        {
            this.brokerUri = connectionUri;
            this.info = info;
            this.requestTimeout = transport.RequestTimeout;
            this.transport = transport;
            this.transport.Command = new CommandHandler(OnCommand);
            this.transport.Exception = new ExceptionHandler(OnException);
            this.transport.Interrupted = new InterruptedHandler(OnTransportInterrupted);
            this.transport.Resumed = new ResumedHandler(OnTransportResumed);
        }

        ~Connection()
        {
            Dispose(false);
        }

        /// <summary>
        /// A delegate that can receive transport level exceptions.
        /// </summary>
        public event ExceptionListener ExceptionListener;

        /// <summary>
        /// An asynchronous listener that is notified when a Fault tolerant connection
        /// has been interrupted.
        /// </summary>
        public event ConnectionInterruptedListener ConnectionInterruptedListener;

        /// <summary>
        /// An asynchronous listener that is notified when a Fault tolerant connection
        /// has been resumed.
        /// </summary>
        public event ConnectionResumedListener ConnectionResumedListener;

        #region Properties

        /// <summary>
        /// This property indicates whether or not async send is enabled.
        /// </summary>
        public bool AsyncSend
        {
            get { return asyncSend; }
            set { asyncSend = value; }
        }

        /// <summary>
        /// This property sets the acknowledgment mode for the connection.
        /// The URI parameter connection.ackmode can be set to a string value
        /// that maps to the enumeration value.
        /// </summary>
        public string AckMode
        {
            set { this.acknowledgementMode = NMSConvert.ToAcknowledgementMode(value); }
        }

        /// <summary>
        /// This property forces all messages that are sent to be sent synchronously overriding
        /// any usage of the AsyncSend flag. This can reduce performance in some cases since the
        /// only messages we normally send synchronously are Persistent messages not sent in a
        /// transaction. This options guarantees that no send will return until the broker has
        /// acknowledge receipt of the message
        /// </summary>
        public bool AlwaysSyncSend
        {
            get { return alwaysSyncSend; }
            set { alwaysSyncSend = value; }
        }

        /// <summary>
        /// This property indicates whether Message's should be copied before being sent via
        /// one of the Connection's send methods.  Copying the Message object allows the user
        /// to resuse the Object over for another send.  If the message isn't copied performance
        /// can improve but the user must not reuse the Object as it may not have been sent
        /// before they reset its payload.
        /// </summary>
        public bool CopyMessageOnSend
        {
            get { return copyMessageOnSend; }
            set { copyMessageOnSend = value; }
        }

        public IConnectionMetaData MetaData
        {
            get { return this.metaData ?? (this.metaData = new ConnectionMetaData()); }
        }

        public Uri BrokerUri
        {
            get { return brokerUri; }
        }

        public ITransport ITransport
        {
            get { return transport; }
            set { this.transport = value; }
        }

        public TimeSpan RequestTimeout
        {
            get { return this.requestTimeout; }
            set { this.requestTimeout = value; }
        }

        public AcknowledgementMode AcknowledgementMode
        {
            get { return acknowledgementMode; }
            set { this.acknowledgementMode = value; }
        }

        public string ClientId
        {
            get { return info.ClientId; }
            set
            {
                if(connected)
                {
                    throw new NMSException("You cannot change the ClientId once the Connection is connected");
                }
                info.ClientId = value;
            }
        }

        public ConnectionId ConnectionId
        {
            get { return info.ConnectionId; }
        }

        /// <summary>
        /// Get/or set the redelivery policy for this connection.
        /// </summary>
        public IRedeliveryPolicy RedeliveryPolicy
        {
            get { return this.redeliveryPolicy; }
            set { this.redeliveryPolicy = value; }
        }

        public PrefetchPolicy PrefetchPolicy
        {
            get { return this.prefetchPolicy; }
            set { this.prefetchPolicy = value; }
        }

        #endregion

        /// <summary>
        /// Starts asynchronous message delivery of incoming messages for this connection.
        /// Synchronous delivery is unaffected.
        /// </summary>
        public void Start()
        {
            CheckConnected();
            if(started.CompareAndSet(false, true))
            {
                lock(sessions.SyncRoot)
                {
                    foreach(Session session in sessions)
                    {
                        session.Start();
                    }
                }
            }
        }

        /// <summary>
        /// This property determines if the asynchronous message delivery of incoming
        /// messages has been started for this connection.
        /// </summary>
        public bool IsStarted
        {
            get { return started.Value; }
        }

        /// <summary>
        /// Temporarily stop asynchronous delivery of inbound messages for this connection.
        /// The sending of outbound messages is unaffected.
        /// </summary>
        public void Stop()
        {
            CheckConnected();
            if(started.CompareAndSet(true, false))
            {
                lock(sessions.SyncRoot)
                {
                    foreach(Session session in sessions)
                    {
                        session.Stop();
                    }
                }
            }
        }

        /// <summary>
        /// Creates a new session to work on this connection
        /// </summary>
        public ISession CreateSession()
        {
            return CreateSession(acknowledgementMode);
        }

        /// <summary>
        /// Creates a new session to work on this connection
        /// </summary>
        public ISession CreateSession(AcknowledgementMode sessionAcknowledgementMode)
        {
            SessionInfo info = CreateSessionInfo(sessionAcknowledgementMode);
            Session session = new Session(this, info, sessionAcknowledgementMode);

            // Set properties on session using parameters prefixed with "session."
            URISupport.CompositeData c = URISupport.parseComposite(this.brokerUri);
            URISupport.SetProperties(session, c.Parameters, "session.");

            if(IsStarted)
            {
                session.Start();
            }

            sessions.Add(session);
            return session;
        }

        internal void RemoveSession(Session session)
        {
            if(!this.closing)
            {
                sessions.Remove(session);
            }
        }

        internal void addDispatcher( ConsumerId id, IDispatcher dispatcher )
        {
            this.dispatchers.Add( id, dispatcher );
        }

        internal void removeDispatcher( ConsumerId id )
        {
            this.dispatchers.Remove( id );
        }

        public void Close()
        {
            lock(myLock)
            {
                if(this.closed)
                {
                    return;
                }

                try
                {
                    Tracer.Info("Closing Connection.");
                    this.closing = true;
                    lock(sessions.SyncRoot)
                    {
                        foreach(Session session in sessions)
                        {
                            session.DoClose();
                        }
                    }
                    sessions.Clear();

                    if(connected)
                    {
                        ShutdownInfo shutdowninfo = new ShutdownInfo();
                        transport.Oneway(shutdowninfo);
                    }

                    Tracer.Info("Disposing of the Transport.");
                    transport.Dispose();
                }
                catch(Exception ex)
                {
                    Tracer.ErrorFormat("Error during connection close: {0}", ex);
                }
                finally
                {
                    this.transport = null;
                    this.closed = true;
                    this.connected = false;
                    this.closing = false;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if(disposed)
            {
                return;
            }

            if(disposing)
            {
                // Dispose managed code here.
            }

            try
            {
                // For now we do not distinguish between Dispose() and Close().
                // In theory Dispose should possibly be lighter-weight and perform a (faster)
                // disorderly close.
                Close();
            }
            catch
            {
                // Ignore network errors.
            }

            disposed = true;
        }

        // Implementation methods

        /// <summary>
        /// Performs a synchronous request-response with the broker
        /// </summary>
        ///

        public Response SyncRequest(Command command)
        {
            try
            {
                return SyncRequest(command, this.RequestTimeout);
            }
            catch(Exception ex)
            {
                throw NMSExceptionSupport.Create(ex);
            }
        }

        public Response SyncRequest(Command command, TimeSpan requestTimeout)
        {
            CheckConnected();

            try
            {
                Response response = transport.Request(command, requestTimeout);
                if(response is ExceptionResponse)
                {
                    ExceptionResponse exceptionResponse = (ExceptionResponse) response;
                    BrokerError brokerError = exceptionResponse.Exception;
                    throw new BrokerException(brokerError);
                }
                return response;
            }
            catch(Exception ex)
            {
                throw NMSExceptionSupport.Create(ex);
            }
        }

        public void Oneway(Command command)
        {
            CheckConnected();

            try
            {
                transport.Oneway(command);
            }
            catch(Exception ex)
            {
                throw NMSExceptionSupport.Create(ex);
            }
        }

        protected void CheckConnected()
        {
            if(closed)
            {
                throw new ConnectionClosedException();
            }

            if(!connected)
            {
                connected = true;
                // now lets send the connection and see if we get an ack/nak
                if(null == SyncRequest(info))
                {
                    closed = true;
                    connected = false;
                    throw new ConnectionClosedException();
                }
            }
        }

        /// <summary>
        /// Handle incoming commands
        /// </summary>
        /// <param name="commandTransport">An ITransport</param>
        /// <param name="command">A  Command</param>
        protected void OnCommand(ITransport commandTransport, Command command)
        {
            if(command is MessageDispatch)
            {
                DispatchMessage((MessageDispatch) command);
            }
            else if(command is ConnectionError)
            {
                if(!closing && !closed)
                {
                    ConnectionError connectionError = (ConnectionError) command;
                    BrokerError brokerError = connectionError.Exception;
                    string message = "Broker connection error.";
                    string cause = "";

                    if(null != brokerError)
                    {
                        message = brokerError.Message;
                        if(null != brokerError.Cause)
                        {
                            cause = brokerError.Cause.Message;
                        }
                    }

                    OnException(commandTransport, new NMSConnectionException(message, cause));
                }
            }
            else
            {
                Tracer.Error("Unknown command: " + command);
            }
        }

        protected void DispatchMessage(MessageDispatch dispatch)
        {
            lock(dispatchers.SyncRoot)
            {
                if(dispatchers.Contains(dispatch.ConsumerId))
                {
                    IDispatcher dispatcher = (IDispatcher) dispatchers[dispatch.ConsumerId];

                    // Can be null when a consumer has sent a MessagePull and there was
                    // no available message at the broker to dispatch.
                    if(dispatch.Message != null)
                    {
                        dispatch.Message.ReadOnlyBody = true;
                        dispatch.Message.ReadOnlyProperties = true;
                        dispatch.Message.RedeliveryCounter = dispatch.RedeliveryCounter;
                    }

                    dispatcher.Dispatch(dispatch);

                    return;
                }
            }

            Tracer.Error("No such consumer active: " + dispatch.ConsumerId);
        }

        protected void OnException(ITransport sender, Exception exception)
        {
            if(ExceptionListener != null && !this.closing)
            {
                try
                {
                    ExceptionListener(exception);
                }
                catch
                {
                    sender.Dispose();
                }
            }
        }

        protected void OnTransportInterrupted(ITransport sender)
        {
            Tracer.Debug("Transport has been Interrupted.");

            foreach(Session session in this.sessions)
            {
                session.ClearMessagesInProgress();
            }

            if(this.ConnectionInterruptedListener != null && !this.closing )
            {
                try
                {
                    this.ConnectionInterruptedListener();
                }
                catch
                {
                }
            }
        }

        protected void OnTransportResumed(ITransport sender)
        {
            Tracer.Debug("Transport has resumed normal operation.");

            if(this.ConnectionResumedListener != null && !this.closing )
            {
                try
                {
                    this.ConnectionResumedListener();
                }
                catch
                {
                }
            }
        }

        internal void OnSessionException(Session sender, Exception exception)
        {
            if(ExceptionListener != null)
            {
                try
                {
                    ExceptionListener(exception);
                }
                catch
                {
                    sender.Close();
                }
            }
        }

        /// <summary>
        /// Creates a new temporary destination name
        /// </summary>
        public String CreateTemporaryDestinationName()
        {
            return info.ConnectionId.Value + ":" + Interlocked.Increment(ref temporaryDestinationCounter);
        }

        /// <summary>
        /// Creates a new local transaction ID
        /// </summary>
        public TransactionId CreateLocalTransactionId()
        {
            TransactionId id = new TransactionId();
            id.ConnectionId = ConnectionId;
            id.Value = Interlocked.Increment(ref localTransactionCounter);
            return id;
        }

        protected SessionInfo CreateSessionInfo(AcknowledgementMode sessionAcknowledgementMode)
        {
            SessionInfo answer = new SessionInfo();
            SessionId sessionId = new SessionId();
            sessionId.ConnectionId = info.ConnectionId.Value;
            sessionId.Value = Interlocked.Increment(ref sessionCounter);
            answer.SessionId = sessionId;
            return answer;
        }
    }
}
