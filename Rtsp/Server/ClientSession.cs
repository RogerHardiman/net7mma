﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Media.Rtp;
using Media.Rtcp;
using Media.Rtsp.Server.Streams;

namespace Media.Rtsp
{
    /// <summary>
    /// Represent the resources in use by remote parties connected to a RtspServer.
    /// </summary>
    internal class ClientSession
    {
        //Needs to have it's own concept of range using the Storage...

        #region Fields

        //Session storage
        //Counters for authenticate and attempts should use static key names, maybe use a dictionary..
        internal System.Collections.Hashtable Storage = System.Collections.Hashtable.Synchronized(new System.Collections.Hashtable());

        /// <summary>
        /// The RtpClient.TransportContext instances which provide valid data to this ClientSession.
        /// </summary>
        internal HashSet<RtpClient.TransportContext> SourceContexts = new HashSet<RtpClient.TransportContext>();

        /// <summary>
        /// A HashSet of SourceStreams attached to the ClientSession which provide the events for Rtp, Rtcp, and Interleaved data.
        /// Instances in this collection are raising events which are being handled in the OnSourcePacketRecieved Method
        /// </summary>
        internal HashSet<SourceStream> AttachedSources = new HashSet<SourceStream>();

        /// <summary>
        /// A one to many collection which is keyed by the source media's SSRC to which subsequently the values are packets which also came from the source
        /// </summary>
        internal Utility.ConcurrentThesaurus<int, RtpPacket> PacketBuffer = new Utility.ConcurrentThesaurus<int, RtpPacket>();

        /// <summary>
        /// This is used to take packets from a source (SourceContexts) and detmerine the TransportContext of the RtpClient to send it to.
        /// </summary>
        internal Dictionary<int, int> RouteDictionary = new Dictionary<int, int>();

        /// <summary>
        /// The server which created this ClientSession
        /// </summary>
        internal RtspServer m_Server;
        
        //The Id of the client
        internal Guid m_Id = Guid.NewGuid();

        /// <summary>
        /// Counters for sent and received bytes
        /// </summary>
        internal int m_Receieved, m_Sent;
        
        //Buffer for data
        internal byte[] m_Buffer;

        internal int m_BufferOffset, m_BufferLength;

        //Sockets
        internal Socket m_RtspSocket;

        //The last response sent to this client session
        internal RtspMessage LastResponse;

        //RtpClient for transport of media
        internal RtpClient m_RtpClient;

        internal byte[] m_SendBuffer;

        #endregion

        #region Properties

        public Guid Id { get; internal set; }

        public string SessionId { get; internal set; }

        public RtspMessage LastRequest { get; internal set; }

        public bool Interleaving { get { return m_RtpClient != null && m_RtpClient.Connected && m_RtspSocket.ProtocolType == m_RtpClient.m_TransportProtocol; } }

        public IPEndPoint LocalEndPoint
        {
            get
            {
                return (IPEndPoint)m_RtspSocket.LocalEndPoint;
            }
        }

        public readonly EndPoint RemoteEndPoint;
           
        #endregion

        #region Constructor

        public ClientSession(RtspServer server, Socket rtspSocket, ArraySegment<byte> buffer = default(ArraySegment<byte>))
        {
            Id = Guid.NewGuid();
            m_Server = server;
            m_RtspSocket = rtspSocket;

            if (buffer == default(ArraySegment<byte>))
                m_Buffer = new byte[m_BufferLength = RtspMessage.MaximumLength];
            else
            {
                m_Buffer = buffer.Array;
                m_BufferOffset = buffer.Offset;
                m_BufferLength = buffer.Count;
            }

            //Assign the remote endPoint
            RemoteEndPoint = rtspSocket.RemoteEndPoint;

            //Begin to receive what is available
            m_RtspSocket.BeginReceiveFrom(m_Buffer, m_BufferOffset, m_BufferLength, SocketFlags.None, ref RemoteEndPoint, new AsyncCallback(m_Server.ProcessReceive), this);
        }

        #endregion

        #region Methods

        public void SendRtspData(byte[] data)
        {
            m_SendBuffer = data;

            if (Interleaving && LastRequest.Method != RtspMethod.SETUP)
            {
                SocketError error;

                int sent = m_RtspSocket.Send(m_SendBuffer, 0, m_SendBuffer.Length, SocketFlags.None, out error);

                while (sent < m_SendBuffer.Length && error != SocketError.ConnectionAborted)
                    sent += m_RtspSocket.Send(m_SendBuffer, sent, m_SendBuffer.Length - sent, SocketFlags.None, out error);
            }
            else m_RtspSocket.BeginSendTo(m_SendBuffer, 0, m_SendBuffer.Length, SocketFlags.None, RemoteEndPoint, new AsyncCallback(m_Server.ProcessSend), this);//Begin to Send the response over the RtspSocket
        }

        RtpClient.TransportContext GetSourceContextForPacket(RtpPacket packet)
        {
            foreach (RtpClient.TransportContext context in SourceContexts)
                if (packet.SynchronizationSourceIdentifier == context.RemoteSynchronizationSourceIdentifier) return context;
            return null;
        }

        /// <summary>
        /// Called for each RtpPacket received in the source RtpClient
        /// </summary>
        /// <param name="client">The RtpClient from which the packet arrived</param>
        /// <param name="packet">The packet which arrived</param>
        internal void OnSourceRtpPacketRecieved(object client, RtpPacket packet)
        {
            try
            {
                //If the packet is null or not allowed then return
                if (packet == null) return;
                else if (PacketBuffer.ContainsKey(packet.SynchronizationSourceIdentifier)) //If the media is paused or in a buffered state
                {                    
                    PacketBuffer.Add(packet.SynchronizationSourceIdentifier, packet);
                }
                else if(RouteDictionary.ContainsKey(packet.SynchronizationSourceIdentifier))//If there is a route for the packet here
                {
                    m_RtpClient.EnquePacket(packet);//Enque the packet to go out
                }
            }
            catch { throw; }
        }

        /// <summary>
        /// Called for each RtcpPacket recevied in the source RtpClient
        /// </summary>
        /// <param name="stream">The listener from which the packet arrived</param>
        /// <param name="packet">The packet which arrived</param>
        internal void OnSourceRtcpPacketRecieved(object stream, RtcpPacket packet)
        {
            try
            {
                //If the source received a goodbye 
                if (packet.Header.PayloadType == Rtcp.GoodbyeReport.PayloadType)
                {
                    //Decode the Goodbye further to determine who the Goodbye was addressed to
                    GoodbyeReport received = new GoodbyeReport(packet);

                    //Go through the chunks to determine 

                    //SourceList
                    foreach (int participantId in received.GetSourceList())
                    {
                        int routedSourceId;
                        //If the chunk is addressed to the source then the media will stop so our client should 
                        if (RouteDictionary.TryGetValue(participantId, out routedSourceId))
                            m_RtpClient.SendGoodbye(m_RtpClient.GetContextBySourceId(routedSourceId));//Disconnect the client from our source
                    }
                }
                else if (packet.Header.PayloadType == Rtcp.SendersReport.PayloadType)
                {

                    //The source stream recieved a senders report                
                    //Update the RtpTimestamp and NtpTimestamp for our clients also
                    SendersReport sr = new SendersReport(packet);

                    //Iterate the blocks of the senders report
                    foreach (Rtcp.ReportBlock rb in sr)
                    {
                        //Determine if report was addressed to a routed source
                        int routedSourceId;

                        //If the chunk is addressed to the source then the media will stop so our client should 
                        if (RouteDictionary.TryGetValue(rb.SendersSynchronizationSourceIdentifier, out routedSourceId))
                        {
                            //Determine if there is a routingContext for the routedId
                            RtpClient.TransportContext routingContext = m_RtpClient.GetContextBySourceId(routedSourceId);

                            //If the routingContext is not null
                            if (routingContext != null)
                            {
                                //Update the routingContext values
                                //The values are in the SendersInformation
                                routingContext.NtpTimestamp = sr.NtpTimestamp;
                                routingContext.RtpTimestamp = sr.RtpTimestamp;
                            }
                        }
                    }
                }
            }
            catch { throw; }
        }

        /// <summary>
        /// Sends the Rtcp Goodbye and detaches all sources
        /// </summary>
        internal void Disconnect()
        {
            try
            {
                //Get rid of any attachment this ClientSession had
                foreach (SourceStream source in AttachedSources.ToArray())
                {
                    RemoveSource(source);
                }

                //Disconnect the RtpClient so it's not hanging around wasting resources for nothing
                if (m_RtpClient != null)
                {
                    m_RtpClient.InterleavedData -= m_Server.ProcessRtspInterleaveData;

                    m_RtpClient.Disconnect();
                    
                    m_RtpClient = null;
                }

                //Close immediately for TCP only
                if(m_RtspSocket.ProtocolType == ProtocolType.Tcp) m_RtspSocket.Close();
            }
            catch { return; }
            
        }

        /// <summary>
        /// Process a Rtsp DESCRIBE.
        /// Re-writes the Sdp.SessionDescription in a manner which contains the values of the server and not of the origional source.
        /// </summary>
        /// <param name="describeRequest">The request received from the server</param>
        /// <param name="source">Tje source stream to describe</param>
        /// <returns>A RtspMessage with a Sdp.SessionDescription in the Body and ContentType set to application/sdp</returns>
        internal RtspMessage ProcessDescribe(RtspMessage describeRequest, SourceStream source)
        {
            RtspMessage describeResponse = CreateRtspResponse(describeRequest);

            if (describeRequest.Location.ToString().ToLowerInvariant().Contains("live"))
            {
                describeResponse.SetHeader(RtspHeaders.ContentBase, "rtsp://" + ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address.ToString() + "/live/" + source.Id + '/');
            }
            else
            {
                describeResponse.SetHeader(RtspHeaders.ContentBase, describeRequest.Location.ToString());
            }

            describeResponse.SetHeader(RtspHeaders.ContentType, Sdp.SessionDescription.MimeType);

            describeResponse.Body = CreateOrUpdateSessionDescription(source).ToString();

            return describeResponse;
        }

        internal RtspMessage ProcessPlay(RtspMessage playRequest, RtpSource source)
        {
            
            ///TODO MAY ALREADY BE PAUSED>....
            ///
            ///If the client was paused then simply calling ProcessPacketBuffer will resume correctly without any further processing required
            ///So long as the Source's RtpClient.TransportContext RtpTimestamp is updated to reflect the value given in the playRequest...
            ///


            //Prepare a place to hold the response
            RtspMessage playResponse = CreateRtspResponse(playRequest);

            //Get the Range header
            string rangeString = playRequest[RtspHeaders.Range];
            TimeSpan? startRange = null, endRange = null;

            #region Range Header Processing (Which really needs some attention)

            //If that is not present we cannot determine where the client wants to start playing from
            if (!string.IsNullOrWhiteSpace(rangeString))
            {
                //Parse Range Header
                string[] times = rangeString.Trim().Split('=');
                if (times.Length > 1)
                {
                    //Determine Format
                    if (times[0] == "npt")//ntp=1.060-20
                    {
                        times = times[1].Split(RtspClient.TimeSplit, StringSplitOptions.RemoveEmptyEntries);
                        if (times[0].ToLowerInvariant() == "now") { }
                        else if (times.Length == 1)
                        {
                            if (times[0].Contains(':'))
                            {
                                startRange = TimeSpan.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                startRange = TimeSpan.FromSeconds(double.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                            }
                        }
                        else if (times.Length == 2)
                        {
                            //Both might not be in the same format? Check spec
                            if (times[0].Contains(':'))
                            {
                                startRange = TimeSpan.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                                endRange = TimeSpan.Parse(times[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                startRange = TimeSpan.FromSeconds(double.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                                endRange = TimeSpan.FromSeconds(double.Parse(times[1].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                            }
                        }
                        else playResponse = CreateRtspResponse(playRequest, RtspStatusCode.InvalidRange);
                    }
                    else if (times[0] == "smpte")//smpte=0:10:20-;time=19970123T153600Z
                    {
                        //Get the times into the times array skipping the time from the server (order may be first so I explicitly did not use Substring overload with count)
                        times = times[1].Split(RtspClient.TimeSplit, StringSplitOptions.RemoveEmptyEntries).Where(s => !s.StartsWith("time=")).ToArray();
                        if (times[0].ToLowerInvariant() == "now") { }
                        else if (times.Length == 1)
                        {
                            startRange = TimeSpan.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else if (times.Length == 2)
                        {
                            startRange = TimeSpan.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            endRange = TimeSpan.Parse(times[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else playResponse = CreateRtspResponse(playRequest, RtspStatusCode.InvalidRange);
                    }
                    else if (times[0] == "clock")//clock=19961108T142300Z-19961108T143520Z
                    {
                        //Get the times into times array
                        times = times[1].Split(RtspClient.TimeSplit, StringSplitOptions.RemoveEmptyEntries);
                        //Check for live
                        if (times[0].ToLowerInvariant() == "now") { }
                        //Check for start time only
                        else if (times.Length == 1)
                        {
                            DateTime now = DateTime.UtcNow, startDate;
                            ///Parse and determine the start time
                            if (DateTime.TryParse(times[0].Trim(), out startDate))
                            {
                                //Time in the past
                                if (now > startDate) startRange = now - startDate;
                                //Future?
                                else startRange = startDate - now;
                            }
                        }
                        else if (times.Length == 2)
                        {
                            DateTime now = DateTime.UtcNow, startDate, endDate;
                            ///Parse and determine the start time
                            if (DateTime.TryParse(times[0].Trim(), out startDate))
                            {
                                //Time in the past
                                if (now > startDate) startRange = now - startDate;
                                //Future?
                                else startRange = startDate - now;
                            }

                            ///Parse and determine the end time
                            if (DateTime.TryParse(times[1].Trim(), out endDate))
                            {
                                //Time in the past
                                if (now > endDate) endRange = now - endDate;
                                //Future?
                                else endRange = startDate - now;
                            }
                        }
                        else playResponse = CreateRtspResponse(playRequest, RtspStatusCode.InvalidRange);
                    }
                    
                    //Add the range header
                    playResponse.SetHeader(RtspHeaders.Range, RtspHeaders.RangeHeader(startRange, endRange));
                }
            }

            #endregion

            //Prepare the RtpInfo header
            //Iterate the source's TransportContext's to Augment the RtpInfo header for the current request

            foreach (RtpClient.TransportContext tc in source.RtpClient.TransportContexts.ToArray()) //Projected here in case modified at during the call
            {
                //Only augment the header for the Sources routed to this ClientSession
                if (!RouteDictionary.ContainsKey(tc.SynchronizationSourceIdentifier)) continue;

                //Make logic to make this clear and simple
                string actualTrack = string.Empty;

                //Get the attributeLine which contains the control attribute
                Sdp.SessionDescriptionLine attributeLine = tc.MediaDescription.Lines.Where(l => l.Type == 'a' && l.Parts.Any(p => p.Contains("control"))).First();

                //If there was a control line present it contains the actual track name
                if (attributeLine != null)
                {
                    actualTrack = attributeLine.Parts.Where(p => p.Contains("control")).FirstOrDefault().Replace("control:", string.Empty);

                    if (!actualTrack.StartsWith(Rtsp.RtspMessage.ReliableTransport) || actualTrack.StartsWith(Rtsp.RtspMessage.UnreliableTransport))
                        actualTrack = "url=rtsp://" + ((IPEndPoint)(m_RtspSocket.LocalEndPoint)).Address + "/live/" + source.Id + actualTrack;
                }

                //Update the RtpInfo header
                playResponse.AppendOrSetHeader(RtspHeaders.RtpInfo, actualTrack + ";seq=" + tc.SequenceNumber + ";rtptime=" + tc.RtpTimestamp); //ssrc= ?
            }

            //Ensure RtpClient is now connected connected so packets will begin to go out when enqued
            if (!m_RtpClient.Connected) m_RtpClient.Connect();

            //Send the SendersReport over Rtcp
            m_RtpClient.SendSendersReports();

            //Ensure events are removed later
            AttachedSources.Add(source);

            //Push out buffered packets first
            ProcessPacketBuffer(source);

            //Attach events
            source.RtpClient.RtcpPacketReceieved += OnSourceRtcpPacketRecieved;
            source.RtpClient.RtpPacketReceieved += OnSourceRtpPacketRecieved;

            //Return the response
            return playResponse;
        }

        /// <summary>
        /// Removes all packets from the PacketBuffer related to the given source and enqueues them on the RtpClient of this ClientSession
        /// </summary>
        /// <param name="source">The RtpSource to check for packets in the PacketBuffer</param>
        internal void ProcessPacketBuffer(RtpSource source)
        {
            //Process packets from the PacketBuffer relevent to the Range Header
            IList<RtpPacket> packets;            

            //Iterate all TransportContext's in the Source
            foreach (RtpClient.TransportContext sourceContext in source.RtpClient.TransportContexts.ToArray())
            {
                if (!PacketBuffer.ContainsKey((int)sourceContext.RemoteSynchronizationSourceIdentifier)) continue;

                packets = PacketBuffer[(int)sourceContext.RemoteSynchronizationSourceIdentifier];

            SendPackets:
                m_RtpClient.m_OutgoingRtpPackets.AddRange(packets.SkipWhile(rtp => rtp.Timestamp < sourceContext.RtpTimestamp));

                
                //If the PacketBuffer has any packets related remove packets from the PacketBuffer
                if (PacketBuffer.Remove((int)sourceContext.RemoteSynchronizationSourceIdentifier, out packets))
                {
                    goto SendPackets;
                }
            }
        }

        /// <summary>
        /// Complete this tomorrow.... Shared Memory
        /// </summary>
        /// <param name="request"></param>
        /// <param name="sourceContext"></param>
        /// <returns></returns>
        internal RtspMessage ProcessSetup(RtspMessage request, RtpSource sourceStream, RtpClient.TransportContext sourceContext)
        {
            Sdp.MediaDescription mediaDescription = sourceContext.MediaDescription;

            bool rtcpDisabled = false;

            //Get the transport header
            string transportHeader = request[RtspHeaders.Transport];

            //If that is not present we cannot determine what transport the client wants
            if (string.IsNullOrWhiteSpace(transportHeader) || !(transportHeader.Contains("RTP")))
            {
                return CreateRtspResponse(request, RtspStatusCode.BadRequest);
            }

            //Get the parts which are delimited by ' ', ';' , '-' or '='
            string[] parts = transportHeader.Split(RtspClient.SpaceSplit[0], RtspClient.TimeSplit[1], RtspClient.TimeSplit[0], RtspClient.EQ);

            string[] channels = null, clientPorts = null;

            //Loop the parts (Exchange for split and then query)
            for (int i = 0, e = parts.Length; i < e; ++i)
            {
                string part = parts[i];

                if (string.IsNullOrWhiteSpace(part)) continue;

                if (part.StartsWith("interleaved"))
                {
                    channels = parts.Skip(++i).Take(2).ToArray();
                    ++i;
                }
                else if (part.StartsWith("client_port"))
                {
                    clientPorts = parts.Skip(++i).Take(2).ToArray();
                    ++i;
                }
            }

            //If there was no way to determine if the client wanted TCP or UDP
            if (clientPorts == null && channels == null)
            {
                return CreateRtspResponse(request, RtspStatusCode.BadRequest);
            }
            
            if (clientPorts != null && sourceStream.ForceTCP)//The client wanted Udp and Tcp was forced
            {
                return CreateRtspResponse(request, RtspStatusCode.BadRequest);
            }

            //We also have to send one back
            string returnTransportHeader = null;

            //Create a unique 32 bit id
            int ssrc = Utility.Random32();

            //We need to make an TransportContext in response to a setup
            RtpClient.TransportContext setupContext = null;

            //Create a response
            RtspMessage response = CreateRtspResponse(request);

            //Check for TCP being forced and then for given udp ports
            if (clientPorts != null) 
            {
                //Tcp was not forced and udp transport was requested
                int rtpPort, rtcpPort;

                //Attempt to parts the ports
                if(!int.TryParse(clientPorts[0].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out rtpPort)
                    || rtpPort > ushort.MaxValue || //And check their ranges
                    !int.TryParse(clientPorts[1].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out rtcpPort)
                    || rtcpPort > ushort.MaxValue)
                {                    
                    response.StatusCode = RtspStatusCode.BadRequest;
                    goto End;
                }
               
                //The client requests Udp .. do this in the session
                if (m_RtpClient == null)
                {
                    //Create a sender
                    m_RtpClient = RtpClient.Sender(((IPEndPoint)m_RtspSocket.LocalEndPoint).Address);

                    //Starts worker thread... 
                    m_RtpClient.Connect();
                }

                //Find an open port to send on (might want to reserve this port with a socket)
                int openPort = Utility.FindOpenPort(ProtocolType.Udp, m_Server.MinimumUdpPort ?? 10000, true);

                if (openPort == -1) throw new RtspServer.RtspServerException("Could not find open Udp Port");
                else if (m_Server.MaximumUdpPort.HasValue && openPort > m_Server.MaximumUdpPort)
                {
                    //Handle port out of range
                    throw new RtspServer.RtspServerException("Open port was out of range");
                }

                //Add the transportChannel
                if (m_RtpClient.TransportContexts.Count == 0)
                {
                    //Use default data and control channel
                    setupContext = new RtpClient.TransportContext(0, 1, ssrc, mediaDescription, !rtcpDisabled);
                }
                else
                {
                    //Have to calculate next data and control channel
                    RtpClient.TransportContext lastContext = m_RtpClient.TransportContexts.Last();
                    setupContext = new RtpClient.TransportContext((byte)(lastContext.DataChannel + 2), (byte)(lastContext.ControlChannel + 2), ssrc, mediaDescription, !rtcpDisabled);
                }

                //Initialize the Udp sockets
                setupContext.InitializeSockets(((IPEndPoint)m_RtspSocket.LocalEndPoint).Address, ((IPEndPoint)m_RtspSocket.RemoteEndPoint).Address, openPort, openPort + 1, rtpPort, rtcpPort);

                //Add the transportChannel
                m_RtpClient.AddTransportContext(setupContext);

                //Create the return Trasnport header
                returnTransportHeader = "RTP/AVP/UDP;unicast;client_port=" + clientPorts[0] + RtspClient.TimeSplit[0] + clientPorts +";server_port=" + setupContext.ClientRtpPort + "-" + setupContext.ClientRtcpPort + ";source=" + ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address + ";ssrc=" + setupContext.SynchronizationSourceIdentifier.ToString("X");                

            }            
            else if(channels.Length == 2) /// Rtsp / Tcp (Interleaved)
            {
                int rtpChannel = 0, rtcpChannel = 1;

                if (!int.TryParse(channels[0].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out rtpChannel)
                    || rtpChannel > byte.MaxValue ||
                    !int.TryParse(channels[1].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out rtcpChannel) ||
                    rtcpChannel > byte.MaxValue)
                {
                    response.StatusCode = RtspStatusCode.BadRequest;
                    goto End;
                }

                //The client requests Tcp
                if (m_RtpClient == null)
                {
                    //Create a new RtpClient
                    m_RtpClient = RtpClient.Interleaved(m_RtspSocket, new ArraySegment<byte>(m_Buffer, m_BufferOffset, m_BufferLength));

                    m_RtpClient.Connect();

                    m_RtpClient.InterleavedData += m_Server.ProcessRtspInterleaveData;

                    //Create a new Interleave
                    setupContext = new RtpClient.TransportContext((byte)rtpChannel, (byte)rtcpChannel, ssrc, mediaDescription, m_RtspSocket, !rtcpDisabled);

                    //Add the transportChannel the client requested
                    m_RtpClient.AddTransportContext(setupContext);

                    //Initialize the Interleaved Socket
                    setupContext.InitializeSockets(m_RtspSocket);
                }
                else if (m_RtpClient != null && m_RtpClient.m_TransportProtocol != ProtocolType.Tcp)//switching From Udp to Tcp
                {
                    //Has Udp source from before switch must clear
                    SourceContexts.Clear();

                    //Re-add the source
                    SourceContexts.Add(sourceContext);

                    //Switch the client to Tcp manually
                    m_RtpClient.m_SocketOwner = false;
                    m_RtpClient.m_TransportProtocol = ProtocolType.Tcp;

                    //Clear the existing transportChannels
                    m_RtpClient.TransportContexts.Clear();

                    //Get rid of existing packets
                    lock (m_RtpClient.m_OutgoingRtpPackets) m_RtpClient.m_OutgoingRtpPackets.Clear();
                    lock (m_RtpClient.m_OutgoingRtcpPackets) m_RtpClient.m_OutgoingRtcpPackets.Clear();

                    //Add the transportChannel the client requested
                    setupContext = new RtpClient.TransportContext((byte)rtpChannel, (byte)rtcpChannel, 0, mediaDescription, m_RtspSocket, !rtcpDisabled);

                    //Add the transportChannel the client requested
                    m_RtpClient.AddTransportContext(setupContext);

                    //Initialize the Interleaved Socket
                    setupContext.InitializeSockets(m_RtspSocket);
                }
                else //Is Tcp not Switching
                {
                    //Have to calculate next data and control channel
                    RtpClient.TransportContext lastContext = m_RtpClient.TransportContexts.Last();
                    setupContext = new RtpClient.TransportContext((byte)(lastContext.DataChannel + 2), (byte)(lastContext.ControlChannel + 2), ssrc, mediaDescription);

                    //Add the transportChannel the client requested
                    m_RtpClient.AddTransportContext(setupContext);

                    //Initialize the current TransportChannel with the interleaved Socket
                    setupContext.InitializeSockets(m_RtspSocket);
                }

                returnTransportHeader = "RTP/AVP/TCP;unicast;interleaved=" + setupContext.DataChannel + '-' + setupContext.ControlChannel + ";ssrc=" + setupContext.SynchronizationSourceIdentifier.ToString("X");
            }
            else//The Transport field did not contain a supported transport specification.
            {
                response.StatusCode = RtspStatusCode.UnsupportedTransport;
                returnTransportHeader = "RTP/AVP";
                goto End;
            }

            //Update the values
            setupContext.NtpTimestamp = sourceContext.NtpTimestamp;
            setupContext.RtpTimestamp = sourceContext.RtpTimestamp;

            //Add the route
            RouteDictionary.Add(sourceContext.SynchronizationSourceIdentifier, setupContext.RemoteSynchronizationSourceIdentifier);
                    
            //Add the source context to this session
            SourceContexts.Add(sourceContext);

        End:
            //Set the returnTransportHeader to the value above 
            response.SetHeader(RtspHeaders.Transport, returnTransportHeader);
            return response;
        }

        internal RtspMessage ProcessPause(RtspMessage request, RtpSource source)
        {
            //If the source is attached
            if (AttachedSources.Contains(source))
            {
                //Iterate the source transport contexts
                foreach (RtpClient.TransportContext sourceContext in source.RtpClient.TransportContexts)
                {
                    //Adding the id will stop the packets from being enqueued into the RtpClient
                    PacketBuffer.Add((int)sourceContext.RemoteSynchronizationSourceIdentifier);
                }

                //Return the response
                return CreateRtspResponse(request);
            }

            //The source is not attached
            return CreateRtspResponse(request, RtspStatusCode.MethodNotValidInThisState);
            
        }

        /// <summary>
        /// Detaches the given SourceStream from the ClientSession
        /// </summary>
        /// <param name="source">The SourceStream to detach</param>
        /// <param name="session">The session to detach from</param>
        internal void RemoveSource(SourceStream source)
        {
            if (source is RtpSource)
            {
                RtpSource rtpSource = source as RtpSource;
                if (rtpSource.RtpClient != null)
                {
                    //For each TransportContext in the RtpClient
                    foreach (RtpClient.TransportContext tc in rtpSource.RtpClient.TransportContexts)
                    {
                        RemoveRoute(tc.MediaDescription); //Detach the SourceStream
                    }

                    //Attach events
                    rtpSource.RtpClient.RtcpPacketReceieved -= OnSourceRtcpPacketRecieved;
                    rtpSource.RtpClient.RtpPacketReceieved -= OnSourceRtpPacketRecieved;
                }
                //Ensure events are removed later
                AttachedSources.Remove(source);
            }
        }

        /// <summary>
        /// Removes an attachment from a ClientSession to the given source where the media desciprtion 
        /// </summary>
        /// <param name="source"></param>
        /// <param name="md"></param>
        /// <param name="session"></param>
        internal void RemoveRoute(Sdp.MediaDescription md)
        {
            //Determine if we have a source which corresponds to the mediaDescription given
            RtpClient.TransportContext sourceContext = SourceContexts.FirstOrDefault(c => c.MediaDescription == md);

            //If the sourceContext is not null
            if (sourceContext != null)
            {
                //Remove the entry from the sessions routing table
                RouteDictionary.Remove(sourceContext.SynchronizationSourceIdentifier);
            }
        }

        internal RtspMessage ProcessTeardown(RtspMessage request, RtpSource source)
        {
            //Determine if this is for only a single track or the entire shebang
            if (!AttachedSources.Contains(source)) return CreateRtspResponse(request, RtspStatusCode.BadRequest);

            //For a single track
            if (request.Location.ToString().Contains("track"))
            {
                //Determine if we have the track
                string track = request.Location.Segments.Last();

                Sdp.MediaDescription mediaDescription = null;

                //bool GetContextBySdpControlLine... out mediaDescription
                RtpClient.TransportContext sourceContext = SourceContexts.FirstOrDefault(c =>
                {
                    Sdp.SessionDescriptionLine attributeLine = c.MediaDescription.Lines.Where(l => l.Type == 'a' && l.Parts.Any(p => p.Contains("control"))).FirstOrDefault();
                    if (attributeLine != null)
                    {
                        string actualTrack = attributeLine.Parts.Where(p => p.Contains("control")).FirstOrDefault().Replace("control:", string.Empty);
                        if (actualTrack == track)
                        {
                            mediaDescription = c.MediaDescription;
                            return true;
                        }
                    }
                    return false;
                });

                //Cannot teardown media because we can't find the track they are asking to tear down
                if (mediaDescription == null)
                {
                    return CreateRtspResponse(request, RtspStatusCode.NotFound);
                }
                else
                {
                    RemoveRoute(mediaDescription);
                }
            }
            else //Tear down all streams
            {
                RemoveSource(source);
                
                if(AttachedSources.Count == 0)
                    m_Server.RemoveSession(this);
            }

            //Return the response
            return CreateRtspResponse(request);
        }

        /// <summary>
        /// Creates a RtspResponse based on the SequenceNumber contained in the given RtspRequest
        /// </summary>
        /// <param name="request">The request to utilize the SequenceNumber from, if null the current SequenceNumber is used</param>
        /// <param name="statusCode">The StatusCode of the generated response</param>
        /// <returns>The RtspResponse created</returns>
        internal RtspMessage CreateRtspResponse(RtspMessage request = null, RtspStatusCode statusCode = RtspStatusCode.OK)
        {
            bool inRequest = request != null;

            RtspMessage response = new RtspMessage(RtspMessageType.Response);
            response.StatusCode = statusCode;

            response.CSeq = request != null ? request.CSeq : LastRequest != null ? LastRequest.CSeq : 1;
            if (!string.IsNullOrWhiteSpace(SessionId))
                response.SetHeader(RtspHeaders.Session, SessionId);

            /*
             RFC2326 - Page57
             * 12.38 Timestamp

               The timestamp general header describes when the client sent the
               request to the server. The value of the timestamp is of significance
               only to the client and may use any timescale. The server MUST echo
               the exact same value and MAY, if it has accurate information about
               this, add a floating point number indicating the number of seconds
               that has elapsed since it has received the request. The timestamp is
               used by the client to compute the round-trip time to the server so
               that it can adjust the timeout value for retransmissions.

               Timestamp  = "Timestamp" ":" *(DIGIT) [ "." *(DIGIT) ] [ delay ]
               delay      =  *(DIGIT) [ "." *(DIGIT) ]
             
             */
            if (inRequest)
            {
                string timestamp = request.GetHeader(RtspHeaders.Timestamp);
                if (!string.IsNullOrWhiteSpace(timestamp))
                {
                    response.SetHeader(RtspHeaders.Timestamp, timestamp);
                    //Calculate Delay?
                }
            }

            return response;
        }

        /// <summary>
        /// Dynamically creates a Sdp.SessionDescription for the given SourceStream using the information already present and only re-writing the necessary values.
        /// </summary>
        /// <param name="stream">The source stream to create a SessionDescription for</param>
        /// <returns>The created SessionDescription</returns>
        internal Sdp.SessionDescription CreateOrUpdateSessionDescription(SourceStream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            //else if (SessionDescription != null) throw new NotImplementedException("There is already a m_SessionDescription for this session, updating is not implemented at this time");

            string sessionId = Utility.DateTimeToNptTimestamp(DateTime.UtcNow).ToString(), sessionVersion = Utility.DateTimeToNptTimestamp(DateTime.UtcNow).ToString();

            string originatorString = "ASTI-Media-Server " + sessionId + " " + sessionVersion + " IN " + (m_RtspSocket.AddressFamily == AddressFamily.InterNetworkV6 ? "IP6 " : "IP4 " ) + ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address.ToString();

            string sessionName = "ASTI Streaming Session"; // + stream.Name 

            Sdp.SessionDescription sdp;

            if (stream is RtpSource)
            {
                RtpSource rtpSource = stream as RtpSource;
                //Make the new SessionDescription
                sdp = new Sdp.SessionDescription(rtpSource.SessionDescription); //Copying the lines and it shouldnt be
            }
            else sdp = new Sdp.SessionDescription(1);
            sdp.SessionName = sessionName;
            sdp.OriginatorAndSessionIdentifier = originatorString;

            string protcol = "rtsp", controlLineBase = "a=control:" + protcol + "://" + ((IPEndPoint)(m_RtspSocket.LocalEndPoint)).Address.ToString() + "/live/" + stream.Id;
            //check for rtspu later...

            //Find an existing control line
            Media.Sdp.SessionDescriptionLine controlLine = sdp.Lines.Where(l => l.Type == 'a' && l.Parts.Any(p => p.Contains("control"))).FirstOrDefault();

            //If there was one rewrite it
            if (controlLine != null)
            {
                sdp.RemoveLine(sdp.Lines.IndexOf(controlLine));
                sdp.Add(new Sdp.SessionDescriptionLine(controlLineBase));
            }

            Sdp.Lines.SessionConnectionLine connectionLine = sdp.Lines.OfType<Sdp.Lines.SessionConnectionLine>().FirstOrDefault();

            //Remove the old connection line
            if (connectionLine != null)
                sdp.RemoveLine(sdp.Lines.IndexOf(connectionLine));

            //Rewrite a new connection line
            string addressString = LocalEndPoint.Address.ToString();

            if (!stream.m_ForceTCP) addressString += "/" + Utility.FindOpenPort(ProtocolType.Udp, m_Server.MinimumUdpPort ?? 10000);

            connectionLine = new Sdp.Lines.SessionConnectionLine()
            {
                Address = addressString,
                AddressType = m_RtspSocket.AddressFamily == AddressFamily.InterNetworkV6 ? "IP6" : "IP4",
                NetworkType = "IN",
            };

            //Add the new line
            sdp.Add(connectionLine, false);

            IEnumerable<Sdp.SessionDescriptionLine> bandwithLines;

            //Iterate the source MediaDescriptions
            foreach (Sdp.MediaDescription md in sdp.MediaDescriptions)
            {               
                //Find a control line
                controlLine = md.Lines.Where(l => l.Type == 'a' && l.Parts.Any(p => p.Contains("control"))).FirstOrDefault();

                //Rewrite it if present to reflect the appropriate MediaDescription
                if (controlLine != null)
                {
                    md.RemoveLine(md.Lines.IndexOf(controlLine));
                    md.Add(new Sdp.SessionDescriptionLine(controlLineBase + '/' + md.MediaType));                                        
                }

                //Remove old bandwith lines
                bandwithLines = md.Lines.Where(l => l.Type == 'b' && l.Parts[0].StartsWith("RR") || l.Parts[0].StartsWith("RS"));

                foreach (Sdp.SessionDescriptionLine line in bandwithLines.ToArray())
                    md.RemoveLine(md.Lines.IndexOf(line));

                if (!stream.m_ForceTCP)
                {
                    md.Add(new Sdp.SessionDescriptionLine("b=RS:96"));
                    md.Add(new Sdp.SessionDescriptionLine("b=RR:96"));
                }
                else if (stream.m_DisableQOS)
                {
                    md.Add(new Sdp.SessionDescriptionLine("b=RS:0"));
                    md.Add(new Sdp.SessionDescriptionLine("b=RR:0"));
                }

            }

            //Clients sessionId is created from the Sdp
            SessionId = sessionId;

            return sdp;
        }

        #endregion                       
    
    }
}
