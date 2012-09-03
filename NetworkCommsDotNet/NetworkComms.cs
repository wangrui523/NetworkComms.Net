﻿//  Copyright 2011 Marc Fletcher, Matthew Dean
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using SerializerBase;
using SerializerBase.Protobuf;
using System.Collections;
using System.Net.NetworkInformation;
using Common.Logging;
using System.Diagnostics;

namespace NetworkCommsDotNet
{
    /// <summary>
    /// NetworkComms.net. C# networking made easy.
    /// </summary>
    public static class NetworkComms
    {
        /// <summary>
        /// Static constructor which sets comm default values
        /// </summary>
        static NetworkComms()
        {
            //Generally comms defaults are defined here
            NetworkNodeIdentifier = ShortGuid.NewGuid();
            NetworkLoadUpdateWindowMS = 200;
            InterfaceLinkSpeed = 100000000;
            DefaultListenPort = 10000;
            ListenOnAllAllowedInterfaces = true;

            ReceiveBufferSizeBytes = 80000;
            SendBufferSizeBytes = 80000;

            CheckSumMismatchSentPacketCacheMaxByteLimit = 75000;

            ConnectionEstablishTimeoutMS = 30000;
            PacketConfirmationTimeoutMS = 5000;
            ConnectionAliveTestTimeoutMS = 1000;

            InternalFixedSendReceiveOptions = new SendReceiveOptions(false, WrappersHelper.Instance.GetSerializer<ProtobufSerializer>(), WrappersHelper.Instance.GetCompressor<NullCompressor>(), ThreadPriority.Normal);
            DefaultSendReceiveOptions = new SendReceiveOptions(false, WrappersHelper.Instance.GetSerializer<ProtobufSerializer>(), WrappersHelper.Instance.GetCompressor<SevenZipLZMACompressor.LZMACompressor>(), ThreadPriority.Normal);
        }

        #region Local Host Information
        /// <summary>
        /// Returns the current machine hostname
        /// </summary>
        public static string HostName
        {
            get { return Dns.GetHostName(); }
        }

        /// <summary>
        /// Setting preferred IP prefixs will direct networkComms.net when selecting ip addresses. An alternative is to set ListenOnAllInterfaces to true.
        /// Correct format is string[] { "192.168", "213.111.10" }.
        /// If multiple prefixs are provided the earlier prefix, if found, takes priority.
        /// </summary>
        public static string[] PreferredIPPrefixs { get; set; }

        /// <summary>
        /// If prefered adaptor names are provided, i.e. { "eth0", "en0", "wlan0" } etc. networkComms.net will only listen on those adaptors.
        /// </summary>
        public static string[] AllowedAdaptorNames { get; set; }

        /// <summary>
        /// Returns all possible ipV4 addresses. Considers networkComms.PreferredIPPrefixs and networkComms.AllowedAdaptorNames. If PreferredIPPrefixs has been set ranks in descending preference. e.g. Most preffered at [0].
        /// </summary>
        /// <returns></returns>
        public static List<IPAddress> AllAvailableLocalIPs()
        {
            //This is probably the most awesome linq expression ever
            //It loops through every known network adaptor and tries to pull out any 
            //ip addresses which match the provided prefixes
            //If multiple matches are found then we rank by prefix order at the end
            //Credit: M.Fletcher & M.Dean

            return (from current in NetworkInterface.GetAllNetworkInterfaces()
                    where
                        //First we need to select interfaces that contain address information
                    (from inside in current.GetIPProperties().UnicastAddresses
                     where (inside.Address.AddressFamily == AddressFamily.InterNetwork || inside.Address.AddressFamily == AddressFamily.InterNetworkV6) &&
                        (AllowedAdaptorNames == null ? true :  AllowedAdaptorNames.Contains(current.Id))
                     //&& (preferredIPPrefix == null ? true : preferredIPPrefix.Contains(inside.Address.ToString(), new IPComparer()))  
                     select inside).Count() > 0
                    //We only want adaptors which are operational
                    //&& current.OperationalStatus == OperationalStatus.Up //This line causes problems in mono
                    select
                    (
                        //Once we have adaptors that contain address information we are after the address
                    from inside in current.GetIPProperties().UnicastAddresses
                    where (inside.Address.AddressFamily == AddressFamily.InterNetwork || inside.Address.AddressFamily == AddressFamily.InterNetworkV6) &&
                        (AllowedAdaptorNames == null ? true : AllowedAdaptorNames.Contains(current.Id))
                    //&& (preferredIPPrefix == null ? true : preferredIPPrefix.Contains(inside.Address.ToString(), new IPComparer()))
                    select inside.Address
                    ).ToArray()).Aggregate(new IPAddress[] { IPAddress.None }, (i, j) => { return i.Union(j).ToArray(); }).OrderBy(ip =>
                    {
                        //If we have no preffered addresses we just return a default
                        if (PreferredIPPrefixs == null)
                            return int.MaxValue;
                        else
                        {
                            //We can check the preffered and return the index at which the IP occurs
                            for (int i = 0; i < PreferredIPPrefixs.Length; i++)
                                if (ip.ToString().StartsWith(PreferredIPPrefixs[i])) return i;

                            //If there was no match for this IP in the preffered IP range we just return maxValue
                            return int.MaxValue;
                        }
                    }).Where(ip => { return ip != IPAddress.None; }).ToList();
        }

        /// <summary>
        /// The default port networkComms.net will operate on
        /// </summary>
        public static int DefaultListenPort { get; set; }

        /// <summary>
        /// The local identifier for this instance of networkComms.net. This is an application unique identifier.
        /// </summary>
        public static ShortGuid NetworkNodeIdentifier { get; private set; }

        /// <summary>
        /// An internal random object
        /// </summary>
        internal static Random randomGen = new Random();

        /// <summary>
        /// A single boolean used to control a networkComms.net shutdown
        /// </summary>
        internal static volatile bool commsShutdown;

        /// <summary>
        /// The number of millisconds over which to take an instance load (CurrentNetworkLoad) to be used in averaged values (AverageNetworkLoad). Default is 200ms but use atleast 100ms to get reliable values.
        /// </summary>
        public static int NetworkLoadUpdateWindowMS { get; set; }
        private static Thread NetworkLoadThread = null;
        private static double currentNetworkLoad;
        private static CommsMath currentNetworkLoadValues;

        /// <summary>
        /// The interface link speed in bits/sec used for network load calculations.
        /// </summary>
        public static long InterfaceLinkSpeed { get; set; }

        /// <summary>
        /// Returns the current instance network usage, as a value between 0 and 1. Returns the largest value for either incoming or outgoing data loads from any available network adaptor. Triggers load analysis upon first call.
        /// </summary>
        public static double CurrentNetworkLoad
        {
            get
            {
                //We start the load thread when we first access the network load
                //this helps cut down on uncessary threads if unrequired
                if (NetworkLoadThread == null)
                {
                    lock (globalDictAndDelegateLocker)
                    {
                        if (NetworkLoadThread == null)
                        {
                            NetworkLoadThread = new Thread(NetworkLoadWorker);
                            NetworkLoadThread.Name = "NetworkLoadThread";
                            NetworkLoadThread.Start();
                        }
                    }
                }

                return currentNetworkLoad;
            }
            private set { currentNetworkLoad = value; }
        }

        /// <summary>
        /// Returns the averaged value of CurrentNetworkLoad, as a value between 0 and 1, for a time window of upto 254 seconds. Triggers load analysis upon first call.
        /// </summary>
        /// <param name="secondsToAverage"></param>
        /// <returns></returns>
        public static double AverageNetworkLoad(byte secondsToAverage)
        {
            if (NetworkLoadThread == null)
            {
                lock (globalDictAndDelegateLocker)
                {
                    if (NetworkLoadThread == null)
                    {
                        currentNetworkLoadValues = new CommsMath();

                        NetworkLoadThread = new Thread(NetworkLoadWorker);
                        NetworkLoadThread.Name = "NetworkLoadThread";
                        NetworkLoadThread.Start();
                    }
                }
            }

            return currentNetworkLoadValues.CalculateMean((int)((secondsToAverage * 1000.0) / NetworkLoadUpdateWindowMS));
        }

        /// <summary>
        /// Takes a network load snapshot (CurrentNetworkLoad) every NetworkLoadUpdateWindowMS
        /// </summary>
        private static void NetworkLoadWorker()
        {
            //Get all interfaces
            NetworkInterface[] interfacesToUse = (from outer in NetworkInterface.GetAllNetworkInterfaces()
                                                  select outer).ToArray();

            long[] startSent, startRecieved, endSent, endRecieved;

            do
            {
                try
                {
                    //we need to look at the load across all adaptors, by default we will probably choose the adaptor with the highest usage
                    DateTime startTime = DateTime.Now;

                    IPv4InterfaceStatistics[] stats = (from current in interfacesToUse select current.GetIPv4Statistics()).ToArray();
                    startSent = (from current in stats select current.BytesSent).ToArray();
                    startRecieved = (from current in stats select current.BytesReceived).ToArray();

                    Thread.Sleep(NetworkLoadUpdateWindowMS);

                    stats = (from current in interfacesToUse select current.GetIPv4Statistics()).ToArray();
                    endSent = (from current in stats select current.BytesSent).ToArray();
                    endRecieved = (from current in stats select current.BytesReceived).ToArray();

                    DateTime endTime = DateTime.Now;

                    List<double> outUsage = new List<double>();
                    List<double> inUsage = new List<double>();
                    for(int i=0; i<startSent.Length; i++)
                    {
                        outUsage.Add((double)(endSent[i] - startSent[i]) / ((double)(InterfaceLinkSpeed * (endTime - startTime).TotalMilliseconds) / 8000));
                        inUsage.Add((double)(endRecieved[i] - startRecieved[i]) / ((double)(InterfaceLinkSpeed * (endTime - startTime).TotalMilliseconds) / 8000));
                    }

                    double loadValue = Math.Max(outUsage.Max(), inUsage.Max());

                    //Limit to one
                    CurrentNetworkLoad = (loadValue > 1 ? 1 : loadValue);
                    currentNetworkLoadValues.AddValue(CurrentNetworkLoad);

                    //We can only have upto 255 seconds worth of data in the average list
                    currentNetworkLoadValues.TrimList((int)(255000.0 / NetworkLoadUpdateWindowMS));
                }
                catch (Exception ex)
                {
                    LogError(ex, "NetworkLoadWorker");
                }
            } while (!commsShutdown);
        }
        #endregion

        #region Established Connections
        /// <summary>
        /// Locker for connection dictionaries
        /// </summary>
        internal static object globalDictAndDelegateLocker = new object();

        /// <summary>
        /// Primary connection dictionary stored by network indentifier
        /// </summary>
        internal static Dictionary<ShortGuid, Dictionary<ConnectionType, List<Connection>>> allConnectionsById = new Dictionary<ShortGuid, Dictionary<ConnectionType, List<Connection>>>();

        /// <summary>
        /// Secondary connection dictionary stored by ip end point. Allows for quick cross referencing.
        /// </summary>
        internal static Dictionary<IPEndPoint, Dictionary<ConnectionType, Connection>> allConnectionsByEndPoint = new Dictionary<IPEndPoint, Dictionary<ConnectionType, Connection>>();

        /// <summary>
        /// Old connection cache so that requests for connectionInfo can be returned even after a connection has been closed.
        /// </summary>
        internal static Dictionary<ShortGuid, Dictionary<ConnectionType, List<ConnectionInfo>>> oldConnectionIdToConnectionInfo = new Dictionary<ShortGuid, Dictionary<ConnectionType, List<ConnectionInfo>>>();
        #endregion

        #region Incoming Data and Connection Config
        /// <summary>
        /// Used for switching between async and sync connectionListen modes. Default is false. No noticable performance difference between the two modes.
        /// </summary>
        public static bool ConnectionListenModeUseSync { get; set; }

        /// <summary>
        /// Used for switching between listening on a single interface or all interfaces. Default is true (all interfaces).
        /// </summary>
        public static bool ListenOnAllAllowedInterfaces { get; set; }

        /// <summary>
        /// Receive data buffer size. Default is 80KB. CAUTION: Changing the default value can lead to severe performance degredation.
        /// </summary>
        public static int ReceiveBufferSizeBytes { get; set; }

        /// <summary>
        /// Send data buffer size. Default is 80KB. CAUTION: Changing the default value can lead to severe performance degredation.
        /// </summary>
        public static int SendBufferSizeBytes { get; set; }
        #endregion

        #region High CPU Usage Tuning
        /// <summary>
        /// In times of high CPU usage we need to ensure that certain time critical functions, like connection handshaking do not timeout.
        /// This sets the thread priority for those processes.
        /// </summary>
        internal static ThreadPriority timeCriticalThreadPriority = ThreadPriority.AboveNormal;
        #endregion

        #region Checksum Config
        /// <summary>
        /// When enabled uses an MD5 checksum to validate all received packets. Default is false, relying on any possible connection checksum alone. 
        /// Also when enabled any packets sent less than CheckSumMismatchSentPacketCacheMaxByteLimit will be cached for a duration to ensure successful delivery.
        /// Default false.
        /// </summary>
        public static bool EnablePacketCheckSumValidation { get; set; }

        /// <summary>
        /// When checksum validation is enabled sets the limit below which sent packets are cached to ensure successful delivery. Default 75KB.
        /// </summary>
        public static int CheckSumMismatchSentPacketCacheMaxByteLimit { get; set; }
        #endregion

        #region PacketType Config and Global Handlers
        /// <summary>
        /// An internal reference copy of all reservedPacketTypeNames.
        /// </summary>
        internal static string[] reservedPacketTypeNames = Enum.GetNames(typeof(ReservedPacketType));

        /// <summary>
        /// Dictionary of all custom packetHandlers. Key is packetType.
        /// </summary>
        static Dictionary<string, List<IPacketTypeHandlerDelegateWrapper>> globalIncomingPacketHandlers = new Dictionary<string, List<IPacketTypeHandlerDelegateWrapper>>();
        
        /// <summary>
        /// Dictionary of any non default custom packet unwrappers. Key is packetType.
        /// </summary>
        static Dictionary<string, PacketTypeUnwrapper> globalIncomingPacketUnwrappers = new Dictionary<string, PacketTypeUnwrapper>();

        /// <summary>
        /// Delegate template for incoming packet handlers.
        /// </summary>
        /// <typeparam name="T">The type of incoming object</typeparam>
        /// <param name="packetHeader">The header associated with the incoming packet</param>
        /// <param name="connection">The connection with which the packet was recieved</param>
        /// <param name="incomingObject">The incoming object of specified type T</param>
        public delegate void PacketHandlerCallBackDelegate<T>(PacketHeader packetHeader, Connection connection, T incomingObject);

        /// <summary>
        /// If true any unknown incoming packet types are ignored. Default is false and will result in an error file being created if an unknown packet type is received.
        /// </summary>
        public static bool IgnoreUnknownPacketTypes { get; set; }

        /// <summary>
        /// Add an incoming packet handler using default SendReceiveOptions. Multiple handlers for the same packet type will be executed in the order they are added.
        /// </summary>
        /// <typeparam name="T">The type of incoming object</typeparam>
        /// <param name="packetTypeStr">The packet type for which this handler will be executed</param>
        /// <param name="packetHandlerDelgatePointer">The delegate to be executed when a packet of packetTypeStr is received</param>
        public static void AppendGlobalIncomingPacketHandler<T>(string packetTypeStr, PacketHandlerCallBackDelegate<T> packetHandlerDelgatePointer)
        {
            AppendGlobalIncomingPacketHandler<T>(packetTypeStr, packetHandlerDelgatePointer, DefaultSendReceiveOptions);
        }

        /// <summary>
        /// Add an incoming packet handler using the provided SendReceiveOptions. Multiple handlers for the same packet type will be executed in the order they are added.
        /// </summary>
        /// <typeparam name="T">The type of incoming object</typeparam>
        /// <param name="packetTypeStr">The packet type for which this handler will be executed</param>
        /// <param name="packetHandlerDelgatePointer">The delegate to be executed when a packet of packetTypeStr is received</param>
        /// <param name="sendReceiveOptions">The SendReceiveOptions to be used for the provided packet type</param>
        public static void AppendGlobalIncomingPacketHandler<T>(string packetTypeStr, PacketHandlerCallBackDelegate<T> packetHandlerDelgatePointer, SendReceiveOptions sendReceiveOptions)
        {
            lock (globalDictAndDelegateLocker)
            {
                //Add the custom serializer and compressor if necessary
                if (sendReceiveOptions.Serializer != null && sendReceiveOptions.Compressor != null)
                {
                    if (globalIncomingPacketUnwrappers.ContainsKey(packetTypeStr))
                    {
                        //Make sure if we already have an existing entry that it matches with the provided
                        if (globalIncomingPacketUnwrappers[packetTypeStr].Options != sendReceiveOptions)
                            throw new PacketHandlerException("You cannot specify a different compressor or serializer instance if one has already been specified for this packetTypeStr.");
                    }
                    else
                        globalIncomingPacketUnwrappers.Add(packetTypeStr, new PacketTypeUnwrapper(packetTypeStr, sendReceiveOptions));
                }
                else if (sendReceiveOptions.Serializer != null ^ sendReceiveOptions.Compressor != null)
                    throw new PacketHandlerException("You must provide both serializer and compressor or neither.");
                else
                {
                    //If we have not specified the serialiser and compressor we assume to be using defaults
                    //If a handler has already been added for this type and has specified specific serialiser and compressor then so should this call to AppendIncomingPacketHandler
                    if (globalIncomingPacketUnwrappers.ContainsKey(packetTypeStr))
                        throw new PacketHandlerException("A handler already exists for this packetTypeStr with specific serializer and compressor instances. Please ensure the same instances are provided in this call to AppendPacketHandler.");
                }

                //Ad the handler to the list
                if (globalIncomingPacketHandlers.ContainsKey(packetTypeStr))
                {
                    //Make sure we avoid duplicates
                    PacketTypeHandlerDelegateWrapper<T> toCompareDelegate = new PacketTypeHandlerDelegateWrapper<T>(packetHandlerDelgatePointer);
                    bool delegateAlreadyExists = (from current in globalIncomingPacketHandlers[packetTypeStr] where current == toCompareDelegate select current).Count() > 0;
                    if (delegateAlreadyExists)
                        throw new PacketHandlerException("This specific packet handler delegate already exists for the provided packetTypeStr.");

                    globalIncomingPacketHandlers[packetTypeStr].Add(new PacketTypeHandlerDelegateWrapper<T>(packetHandlerDelgatePointer));
                }
                else
                    globalIncomingPacketHandlers.Add(packetTypeStr, new List<IPacketTypeHandlerDelegateWrapper>() { new PacketTypeHandlerDelegateWrapper<T>(packetHandlerDelgatePointer) });

                if (loggingEnabled) logger.Info("Added incoming packetHandler for '" + packetTypeStr + "' packetType.");
            }
        }

        /// <summary>
        /// Removes the provided delegate for the specified packet type.
        /// </summary>
        /// <param name="packetTypeStr">The packet type for which the delegate will be removed</param>
        /// <param name="packetHandlerDelgatePointer">The delegate to be removed</param>
        public static void RemoveGlobalIncomingPacketHandler(string packetTypeStr, Delegate packetHandlerDelgatePointer)
        {
            lock (globalDictAndDelegateLocker)
            {
                if (globalIncomingPacketHandlers.ContainsKey(packetTypeStr))
                {
                    //Remove any instances of this handler from the delegates
                    //The bonus here is if the delegate has not been added we continue quite happily
                    IPacketTypeHandlerDelegateWrapper toRemove = null;

                    foreach (var handler in globalIncomingPacketHandlers[packetTypeStr])
                    {
                        if (handler.EqualsDelegate(packetHandlerDelgatePointer))
                        {
                            toRemove = handler;
                            break;
                        }
                    }

                    if (toRemove != null)
                        globalIncomingPacketHandlers[packetTypeStr].Remove(toRemove);

                    if (globalIncomingPacketHandlers[packetTypeStr] == null || globalIncomingPacketHandlers[packetTypeStr].Count == 0)
                    {
                        globalIncomingPacketHandlers.Remove(packetTypeStr);

                        //Remove any entries in the unwrappers dict as well as we are done with this packetTypeStr
                        if (globalIncomingPacketUnwrappers.ContainsKey(packetTypeStr))
                            globalIncomingPacketUnwrappers.Remove(packetTypeStr);

                        if (loggingEnabled) logger.Info("Removed a packetHandler for '" + packetTypeStr + "' packetType. No handlers remain.");
                    }
                    else
                        if (loggingEnabled) logger.Info("Removed a packetHandler for '" + packetTypeStr + "' packetType. Handlers remain.");
                }
            }
        }

        /// <summary>
        /// Removes all delegates for the provided packet type.
        /// </summary>
        /// <param name="packetTypeStr">Packet type for which all delegates should be removed</param>
        public static void RemoveAllCustomGlobalPacketHandlers(string packetTypeStr)
        {
            lock (globalDictAndDelegateLocker)
            {
                //We don't need to check for potentially removing a critical reserved packet handler here because those cannot be removed.
                if (globalIncomingPacketHandlers.ContainsKey(packetTypeStr))
                {
                    globalIncomingPacketHandlers.Remove(packetTypeStr);

                    if (loggingEnabled) logger.Info("Removed all incoming packetHandlers for '" + packetTypeStr + "' packetType.");
                }
            }
        }

        /// <summary>
        /// Removes all delegates for all packet types
        /// </summary>
        public static void RemoveAllCustomGlobalPacketHandlers()
        {
            lock (globalDictAndDelegateLocker)
            {
                globalIncomingPacketHandlers = new Dictionary<string, List<IPacketTypeHandlerDelegateWrapper>>();

                if (loggingEnabled) logger.Info("Removed all incoming packetHandlers for all packetTypes");
            }
        }

        /// <summary>
        /// Trigger incoming packet delegates for the provided parameters.
        /// </summary>
        /// <param name="packetHeader">The packet header</param>
        /// <param name="connection">The incoming connection</param>
        /// <param name="incomingObjectBytes">The bytes corresponding to the incoming object</param>
        /// <param name="options">The SendReceiveOptions to be used to convert incomingObjectBytes back to the desired object</param>
        public static void TriggerGlobalPacketHandlers(PacketHeader packetHeader, Connection connection, byte[] incomingObjectBytes, SendReceiveOptions options)
        {
            TriggerGlobalPacketHandlers(packetHeader, connection, incomingObjectBytes, options);
        }

        /// <summary>
        /// Trigger incoming packet delegates for the provided parameters.
        /// </summary>
        /// <param name="packetHeader">The packet header</param>
        /// <param name="connection">The incoming connection</param>
        /// <param name="incomingObjectBytes">The bytes corresponding to the incoming object</param>
        /// <param name="options">The SendReceiveOptions to be used to convert incomingObjectBytes back to the desired object</param>
        /// <param name="ignoreUnknownPacketTypeOverride">Used to potentially override NetworkComms.IgnoreUnknownPacketTypes property</param>
        internal static void TriggerGlobalPacketHandlers(PacketHeader packetHeader, Connection connection, byte[] incomingObjectBytes, SendReceiveOptions options, bool ignoreUnknownPacketTypeOverride = false)
        {
            try
            {
                if (options == null) throw new PacketHandlerException("Provided sendReceiveOptions should not be null for packetType " + packetHeader.PacketType);

                //We take a copy of the handlers list incase it is modified outside of the lock
                List<IPacketTypeHandlerDelegateWrapper> handlersCopy = null;
                lock (globalDictAndDelegateLocker)
                    if (globalIncomingPacketHandlers.ContainsKey(packetHeader.PacketType))
                        handlersCopy = new List<IPacketTypeHandlerDelegateWrapper>(globalIncomingPacketHandlers[packetHeader.PacketType]);

                if (handlersCopy == null && !IgnoreUnknownPacketTypes && !ignoreUnknownPacketTypeOverride)
                {
                    //We may get here if we have not added any custom delegates for reserved packet types
                    if (!reservedPacketTypeNames.Contains(packetHeader.PacketType))
                    {
                        //Change this to just a log because generally a packet of the wrong type is nothing to really worry about
                        if (NetworkComms.loggingEnabled) NetworkComms.logger.Warn("The received packet type '" + packetHeader.PacketType + "' has no configured handler and network comms is not set to ignore unknown packet types. Set NetworkComms.IgnoreUnknownPacketTypes=true to prevent this error.");
                        LogError(new UnexpectedPacketTypeException("The received packet type '" + packetHeader.PacketType + "' has no configured handler and network comms is not set to ignore unknown packet types. Set NetworkComms.IgnoreUnknownPacketTypes=true to prevent this error."), "PacketHandlerErrorGlobal_" + packetHeader.PacketType);
                    }

                    return;
                }
                else if (handlersCopy == null && (IgnoreUnknownPacketTypes || ignoreUnknownPacketTypeOverride))
                    //If we have received and unknown packet type and we are choosing to ignore them we just finish here
                    return;
                else
                {
                    //Idiot check
                    if (handlersCopy.Count == 0)
                        throw new PacketHandlerException("An entry exists in the packetHandlers list but it contains no elements. This should not be possible.");

                    //Deserialise the object only once
                    object returnObject = handlersCopy[0].DeSerialize(incomingObjectBytes, options);

                    //Pass the data onto the handler and move on.
                    if (loggingEnabled) logger.Trace(" ... passing completed data packet to selected handlers.");

                    //Pass the object to all necessary delgates
                    //We need to use a copy because we may modify the original delegate list during processing
                    foreach (IPacketTypeHandlerDelegateWrapper wrapper in handlersCopy)
                    {
                        try
                        {
                            wrapper.Process(packetHeader, connection, returnObject);
                        }
                        catch (Exception ex)
                        {
                            if (NetworkComms.loggingEnabled) NetworkComms.logger.Fatal("An unhandled exception was caught while processing a packet handler for a packet type '" + packetHeader.PacketType + "'. Make sure to catch errors in packet handlers. See error log file for more information.");
                            NetworkComms.LogError(ex, "PacketHandlerErrorGlobal_" + packetHeader.PacketType);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //If anything goes wrong here all we can really do is log the exception
                if (NetworkComms.loggingEnabled) NetworkComms.logger.Fatal("An exception occured in TriggerPacketHandler() for a packet type '" + packetHeader.PacketType + "'. See error log file for more information.");
                NetworkComms.LogError(ex, "PacketHandlerErrorGlobal_" + packetHeader.PacketType);
            }
        }

        /// <summary>
        /// Returns the unwrapper sendReceiveOptions for the provided packet type. If no specific options are registered returns null.
        /// </summary>
        /// <param name="packetTypeStr"></param>
        /// <returns></returns>
        public static SendReceiveOptions PacketTypeGlobalUnwrapperOptions(string packetTypeStr)
        {
            SendReceiveOptions options = null;

            //If we find a global packet unwrapper for this packetType we used those options
            lock (globalDictAndDelegateLocker)
            {
                if (globalIncomingPacketUnwrappers.ContainsKey(packetTypeStr))
                    options = globalIncomingPacketUnwrappers[packetTypeStr].Options;
            }

            return options;
        }

        /// <summary>
        /// Returns true if a global packet handler exists for the provided packet type.
        /// </summary>
        /// <param name="packetTypeStr"></param>
        /// <returns></returns>
        public static bool GlobalIncomingPacketHandlerExists(string packetTypeStr)
        {
            lock (globalDictAndDelegateLocker)
                return globalIncomingPacketHandlers.ContainsKey(packetTypeStr);
        }
        #endregion

        #region Connection Establish and Shutdown
        /// <summary>
        /// Delegate template for connection shutdown delegates.
        /// </summary>
        public delegate void ConnectionEstablishShutdownDelegate(Connection connection);

        /// <summary>
        /// Multicast delegate pointer for connection shutdowns.
        /// </summary>
        internal static ConnectionEstablishShutdownDelegate globalConnectionShutdownDelegates;

        /// <summary>
        /// Multicast delegate pointer for connection establishments.
        /// </summary>
        internal static ConnectionEstablishShutdownDelegate globalConnectionEstablishDelegates;

        /// <summary>
        /// Comms shutdown event. This will be triggered when calling NetworkComms.Shutdown
        /// </summary>
        public static event EventHandler<EventArgs> OnCommsShutdown;

        /// <summary>
        /// Add a new connection shutdown delegate which will be called for every connection as it is closes.
        /// </summary>
        /// <param name="connectionShutdownDelegate"></param>
        public static void AppendGlobalConnectionCloseHandler(ConnectionEstablishShutdownDelegate connectionShutdownDelegate)
        {
            lock (globalDictAndDelegateLocker)
            {
                if (globalConnectionShutdownDelegates == null)
                    globalConnectionShutdownDelegates = connectionShutdownDelegate;
                else
                    globalConnectionShutdownDelegates += connectionShutdownDelegate;

                if (loggingEnabled) logger.Info("Added globalConnectionShutdownDelegates.");
            }
        }

        /// <summary>
        /// Remove a connection shutdown delegate.
        /// </summary>
        /// <param name="connectionShutdownDelegate"></param>
        public static void RemoveGlobalConnectionCloseHandler(ConnectionEstablishShutdownDelegate connectionShutdownDelegate)
        {
            lock (globalDictAndDelegateLocker)
            {
                globalConnectionShutdownDelegates -= connectionShutdownDelegate;

                if (loggingEnabled) logger.Info("Removed globalConnectionShutdownDelegates.");

                if (globalConnectionShutdownDelegates == null)
                {
                    if (loggingEnabled) logger.Info("No handlers remain for globalConnectionShutdownDelegates.");
                }
                else
                {
                    if (loggingEnabled) logger.Info("Handlers remain for globalConnectionShutdownDelegates.");
                }
            }
        }

        /// <summary>
        /// Add a new connection establish delegate which will be called for every connection once it has been succesfully established.
        /// </summary>
        /// <param name="connectionShutdownDelegate"></param>
        public static void AppendGlobalConnectionEstablishHandler(ConnectionEstablishShutdownDelegate connectionEstablishDelegate)
        {
            lock (globalDictAndDelegateLocker)
            {
                if (globalConnectionEstablishDelegates == null)
                    globalConnectionEstablishDelegates = connectionEstablishDelegate;
                else
                    globalConnectionEstablishDelegates += connectionEstablishDelegate;

                if (loggingEnabled) logger.Info("Added globalConnectionEstablishDelegates.");
            }
        }

        /// <summary>
        /// Remove a connection establish delegate.
        /// </summary>
        /// <param name="connectionShutdownDelegate"></param>
        public static void RemoveGlobalConnectionEstablishHandler(ConnectionEstablishShutdownDelegate connectionEstablishDelegate)
        {
            lock (globalDictAndDelegateLocker)
            {
                globalConnectionEstablishDelegates -= connectionEstablishDelegate;

                if (loggingEnabled) logger.Info("Removed globalConnectionEstablishDelegates.");

                if (globalConnectionEstablishDelegates == null)
                {
                    if (loggingEnabled) logger.Info("No handlers remain for globalConnectionEstablishDelegates.");
                }
                else
                {
                    if (loggingEnabled) logger.Info("Handlers remain for globalConnectionEstablishDelegates.");
                }
            }
        }

        /// <summary>
        /// Shutdown all connections, comms threads and execute OnCommsShutdown event. If any comms activity has taken place this should be called on application close.
        /// </summary>
        public static void Shutdown(int threadShutdownTimeoutMS = 1000)
        {
            commsShutdown = true;

            Connection.ShutdownBase(threadShutdownTimeoutMS);
            TCPConnection.Shutdown(threadShutdownTimeoutMS);
            UDPConnection.Shutdown();

            try
            {
                CloseAllConnections();
            }
            catch (CommsException)
            {

            }
            catch (Exception ex)
            {
                LogError(ex, "CommsShutdownError");
            }

            try
            {
                if (NetworkLoadThread != null)
                {
                    if (!NetworkLoadThread.Join(threadShutdownTimeoutMS))
                    {
                        NetworkLoadThread.Abort();
                        throw new CommsSetupShutdownException("Timeout waiting for NetworkLoadThread thread to shutdown after " + threadShutdownTimeoutMS + " ms. ");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "CommsShutdownError");
            }

            try
            {
                if (OnCommsShutdown != null) OnCommsShutdown(null, new EventArgs());
            }
            catch (Exception ex)
            {
                LogError(ex, "CommsShutdownError");
            }

            commsShutdown = false;
            if (loggingEnabled) logger.Info("Network comms has shutdown");
        }
        #endregion

        #region Timeouts
        /// <summary>
        /// Time to wait in milliseconds before throwing an exception when waiting for a connection to be established. Default is 30000.
        /// </summary
        public static int ConnectionEstablishTimeoutMS { get; set; }

        /// <summary>
        /// Time to wait in milliseconds before throwing an exception when waiting for confirmation of packet receipt. Default is 5000.
        /// </summary>
        public static int PacketConfirmationTimeoutMS { get; set; }

        /// <summary>
        /// Time to wait in milliseconds before assuming a remote connection is dead when doing a connection test. Default is 1000.
        /// </summary>
        public static int ConnectionAliveTestTimeoutMS { get; set; }
        #endregion

        #region Logging
        internal static bool loggingEnabled = false;
        internal static ILog logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Access the logger externally.
        /// </summary>
        public static ILog Logger
        {
            get { return logger; }
        }

        /// <summary>
        /// Enable logging using the provided common.logging adaptor. See examples for usage.
        /// </summary>
        /// <param name="loggingAdaptor"></param>
        public static void EnableLogging(ILoggerFactoryAdapter loggingAdaptor)
        {
            lock (globalDictAndDelegateLocker)
            {
                loggingEnabled = true;
                Common.Logging.LogManager.Adapter = loggingAdaptor;
                logger = LogManager.GetCurrentClassLogger();
            }
        }

        /// <summary>
        /// Disable logging in networkComms
        /// </summary>
        public static void DisableLogging()
        {
            lock (globalDictAndDelegateLocker)
            {
                loggingEnabled = false;
                Common.Logging.LogManager.Adapter = new Common.Logging.Simple.NoOpLoggerFactoryAdapter();
            }
        }

        /// <summary>
        /// Locker for LogError() which ensures thread safe saves.
        /// </summary>
        static object errorLocker = new object();

        /// <summary>
        /// Appends the provided logString to end of fileName.txt
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="logString"></param>
        public static void AppendStringToLogFile(string fileName, string logString)
        {
            try
            {
                lock (errorLocker)
                {
                    using (System.IO.StreamWriter sw = new System.IO.StreamWriter(fileName + ".txt", true))
                        sw.WriteLine(logString);
                }
            }
            catch (Exception)
            {
                //If an error happens here, such as if the file is locked then we lucked out.
            }
        }

        /// <summary>
        /// Logs provided exception to a file to assist troubleshooting.
        /// </summary>
        public static string LogError(Exception ex, string fileAppendStr, string optionalCommentStr = "")
        {
            string fileName;

            lock (errorLocker)
            {
                if (loggingEnabled) logger.Fatal(fileAppendStr + (optionalCommentStr != "" ? " - " + optionalCommentStr : ""), ex);

#if iOS
                fileName = fileAppendStr + " " + DateTime.Now.Hour.ToString() + "." + DateTime.Now.Minute.ToString() + "." + DateTime.Now.Second.ToString() + "." + DateTime.Now.Millisecond.ToString() + " " + DateTime.Now.ToString("dd-MM-yyyy" + " [" + Thread.CurrentContext.ContextID + "]");
#else
                fileName = fileAppendStr + " " + DateTime.Now.Hour.ToString() + "." + DateTime.Now.Minute.ToString() + "." + DateTime.Now.Second.ToString() + "." + DateTime.Now.Millisecond.ToString() + " " + DateTime.Now.ToString("dd-MM-yyyy" + " [" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Thread.CurrentContext.ContextID + "]");
#endif

                try
                {
                    using (System.IO.StreamWriter sw = new System.IO.StreamWriter(fileName + ".txt", false))
                    {
                        if (optionalCommentStr != "")
                        {
                            sw.WriteLine("Comment: " + optionalCommentStr);
                            sw.WriteLine("");
                        }

                        if (ex.GetBaseException() != null)
                            sw.WriteLine("Base Exception Type: " + ex.GetBaseException().ToString());

                        if (ex.InnerException != null)
                            sw.WriteLine("Inner Exception Type: " + ex.InnerException.ToString());

                        if (ex.StackTrace != null)
                        {
                            sw.WriteLine("");
                            sw.WriteLine("Stack Trace: " + ex.StackTrace.ToString());
                        }
                    }
                }
                catch (Exception)
                {
                    //This should never really happen, but just incase.
                }
            }

            return fileName;
        }
        #endregion

        #region Serializers and Compressors
        private static Dictionary<Type, ISerialize> allKnownSerializers = WrappersHelper.Instance.GetAllSerializes();
        private static Dictionary<Type, ICompress> allKnownCompressors = WrappersHelper.Instance.GetAllCompressors();

        /// <summary>
        /// The following are used for internal comms objects, packet headers, connection establishment etc. 
        /// We generally seem to increase the size of our data if compressing small objects (~50 bytes)
        /// Given the typical header size is 40 bytes we might as well not compress these objects.
        /// </summary>
        internal static SendReceiveOptions InternalFixedSendReceiveOptions { get; set; }

        /// <summary>
        /// Default options for sending and receiving in the absence of specific values
        /// </summary>
        public static SendReceiveOptions DefaultSendReceiveOptions { get; set; }
        #endregion

        #region Connection Access
        /// <summary>
        /// Send the provided object to the specified destination using TCP. Uses default sendReceiveOptions and port. For more control over options see connection specific methods.
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use for send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="sendObject">The obect to send</param>
        public static void SendObject(string packetTypeStr, string destinationIPAddress, object sendObject)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            conn.SendObject(packetTypeStr, sendObject);
        }

        /// <summary>
        /// Send the provided object to the specified destination and wait for a return object using TCP. Uses default sendReceiveOptions and port. For more control over options see connection specific methods.
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <returns>The expected return object</returns>
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject);
        }

        /// <summary>
        /// Return the MD5 hash of the provided byte array as a string
        /// </summary>
        /// <param name="bytesToMd5"></param>
        /// <returns></returns>
        public static string MD5Bytes(byte[] bytesToMd5)
        {
            System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(bytesToMd5)).Replace("-", "");
        }

        /// <summary>
        /// Returns a ConnectionInfo array containing information for all connections
        /// </summary>
        /// <returns></returns>
        public static ConnectionInfo[] AllConnectionInfos()
        {
            lock (globalDictAndDelegateLocker)
            {
                return (from current in allConnectionsByEndPoint
                        select current.Value.Values.Select(connection =>
                        {
                            return connection.ConnectionInfo;
                        })).Aggregate(new List<ConnectionInfo> { null }, (left, right) => { return left.Union(right).ToList(); }).Where(entry => { return entry != null; }).ToArray();
            }
        }

        /// <summary>
        /// Returns the total number of connections
        /// </summary>
        /// <returns></returns>
        public static int TotalNumConnections()
        {
            lock (globalDictAndDelegateLocker)
                return (from current in allConnectionsByEndPoint select current.Value.Count).Sum();
        }

        /// <summary>
        /// Returns the total number of connections where the remoteEndPoint.Address matches the provided IP address
        /// </summary>
        /// <param name="matchIP">The IP address to match</param>
        /// <returns></returns>
        public static int TotalNumConnections(IPAddress matchIP)
        {
            lock (globalDictAndDelegateLocker)
            {
                return (from current in allConnectionsByEndPoint
                        select current.Value.Count(connection => { return connection.Value.ConnectionInfo.RemoteEndPoint.Address.Equals(matchIP); })).Sum();
            }
        }

        /// <summary>
        /// Close all connections
        /// </summary>
        public static void CloseAllConnections()
        {
            CloseAllConnections(ConnectionType.Undefined, new IPEndPoint[0]);
        }

        /// <summary>
        /// Close all connections of the provided type, e.g. TCP, UDP etc
        /// </summary>
        /// <param name="connectionType">The type of connections to be closed</param>
        public static void CloseAllConnections(ConnectionType connectionType)
        {
            CloseAllConnections(connectionType, new IPEndPoint[0]);
        }

        /// <summary>
        /// Close all connections of the provided type except to provided endPoints.
        /// </summary>
        /// <param name="connectionTypeToClose">The type of connections to be closed. ConnectionType.Undefined matches ALL connection types</param>
        /// <param name="closeAllExceptTheseEndPoints">Close all except those with provided endPoints</param>
        public static void CloseAllConnections(ConnectionType connectionTypeToClose, IPEndPoint[] closeAllExceptTheseEndPoints)
        {
            List<Connection> connectionsToClose;

            lock (globalDictAndDelegateLocker)
            {
                connectionsToClose = (from current in allConnectionsByEndPoint.Values
                                      select (from inner in current
                                              where (connectionTypeToClose == ConnectionType.Undefined ? true : inner.Key == connectionTypeToClose)
                                              where !closeAllExceptTheseEndPoints.Contains(inner.Value.ConnectionInfo.RemoteEndPoint)
                                              select inner.Value)).Aggregate(new List<Connection>() { null }, (left, right) => { return left.Union(right).ToList(); }).Where(entry => { return entry != null; }).ToList();
            }

            foreach (Connection connection in connectionsToClose)
                connection.CloseConnection(false, -6);
        }

        /// <summary>
        /// Returns a list of all connections matching the provided connectionType
        /// </summary>
        /// <param name="connectionType">The type of connections to return. ConnectionType.Undefined matches ALL connection types</param>
        /// <returns></returns>
        public static List<Connection> RetrieveConnection(ConnectionType connectionType)
        {
            lock (globalDictAndDelegateLocker)
            {
                return (from current in allConnectionsByEndPoint.Values
                        select (from inner in current
                                where (connectionType == ConnectionType.Undefined ? true : inner.Key == connectionType)
                                select inner.Value)).Aggregate(new List<Connection>() { null }, (left, right) => { return left.Union(right).ToList(); }).Where(entry => {return entry != null;}).ToList();
            }
        }

        /// <summary>
        /// Returns a list of all connections
        /// </summary>
        /// <returns></returns>
        public static List<Connection> RetrieveConnection()
        {
            return RetrieveConnection(ConnectionType.Undefined);
        }

        /// <summary>
        /// Retrieve a list of existing connections with the provided connectionId of the provided ConnectionType. Returns null if the requested connections do not exist.
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="connectionType"></param>
        /// <returns></returns>
        public static List<Connection> RetrieveConnection(ShortGuid connectionId, ConnectionType connectionType)
        {
            lock (globalDictAndDelegateLocker)
                return (from current in NetworkComms.allConnectionsById where current.Key == connectionId && current.Value.ContainsKey(connectionType) select current.Value[connectionType]).FirstOrDefault();
        }

        /// <summary>
        /// Retrieve an existing connection with the provided ConnectionInfo. Returns null if the requested connection does not exist.
        /// </summary>
        /// <param name="connectionInfo"></param>
        /// <returns></returns>
        public static Connection RetrieveConnection(ConnectionInfo connectionInfo)
        {
            lock (globalDictAndDelegateLocker)
                return (from current in NetworkComms.allConnectionsByEndPoint where current.Key.Equals(connectionInfo.RemoteEndPoint) && current.Value.ContainsKey(connectionInfo.ConnectionType) select current.Value[connectionInfo.ConnectionType]).FirstOrDefault();
        }

        /// <summary>
        /// Retrieve an existing connection with the provided IPEndPoint of the provided ConnectionType. Returns null if the requested connection does not exist.
        /// </summary>
        /// <param name="IPEndPoint"></param>
        /// <param name="connectionType"></param>
        /// <returns></returns>
        public static Connection RetrieveConnection(IPEndPoint IPEndPoint, ConnectionType connectionType)
        {
            lock (globalDictAndDelegateLocker)
            {
                //return (from current in NetworkComms.allConnectionsByEndPoint where current.Key == IPEndPoint && current.Value.ContainsKey(connectionType) select current.Value[connectionType]).FirstOrDefault();
                //return (from current in NetworkComms.allConnectionsByEndPoint where current.Key == IPEndPoint select current.Value[connectionType]).FirstOrDefault();
                if (allConnectionsByEndPoint.ContainsKey(IPEndPoint))
                {
                    if (allConnectionsByEndPoint[IPEndPoint].ContainsKey(connectionType))
                        return allConnectionsByEndPoint[IPEndPoint][connectionType];
                    else
                        return null;
                }
                else
                    return null;
            }
        }

        /// <summary>
        /// Returns true if a connection exists with the provided connectionId and ConnectionType
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="connectionType"></param>
        /// <returns></returns>
        public static bool ConnectionExists(ShortGuid connectionId, ConnectionType connectionType)
        {
            if (loggingEnabled) logger.Trace("Checking by identifier and endPoint for existing " + connectionType + " connection to " + connectionId);

            lock (globalDictAndDelegateLocker)
            {
                if (allConnectionsById.ContainsKey(connectionId))
                {
                    if (allConnectionsById[connectionId].ContainsKey(connectionType))
                        return allConnectionsById[connectionId][connectionType].Count() > 0;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if a connection exists with the provided IPEndPoint and ConnectionType
        /// </summary>
        /// <param name="remoteEndPoint"></param>
        /// <param name="connectionType"></param>
        /// <returns></returns>
        public static bool ConnectionExists(IPEndPoint remoteEndPoint, ConnectionType connectionType)
        {
            if (loggingEnabled) logger.Trace("Checking by endPoint for existing " + connectionType + " connection to " + remoteEndPoint.Address + ":" + remoteEndPoint.Port);

            lock (globalDictAndDelegateLocker)
            {
                if (allConnectionsByEndPoint.ContainsKey(remoteEndPoint))
                    return allConnectionsByEndPoint[remoteEndPoint].ContainsKey(connectionType);
                else
                    return false;
            }
        }

        /// <summary>
        /// Removes the reference to the provided connection from within networkComms. DOES NOT CLOSE THE CONNECTION. Returns true if the provided connection reference existed and was removed, false otherwise.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="maintainConnectionInfoHistory"></param>
        /// <returns></returns>
        internal static bool RemoveConnectionReference(Connection connection, bool maintainConnectionInfoHistory = true)
        {
            //We don't have the connection identifier until the connection has been established.
            if (!connection.ConnectionInfo.ConnectionEstablished && !connection.ConnectionInfo.ConnectionShutdown)
                return false;

            if (connection.ConnectionInfo.ConnectionEstablished && !connection.ConnectionInfo.ConnectionShutdown)
                throw new ConnectionShutdownException("A connection can only be removed once correctly shutdown.");

            bool returnValue = false;

            //Ensure connection references are removed from networkComms
            //Once we think we have closed the connection it's time to get rid of our other references
            lock (globalDictAndDelegateLocker)
            {
                #region Update NetworkComms Connection Dictionaries
                //We establish whether we have already done this step
                if ((allConnectionsById.ContainsKey(connection.ConnectionInfo.NetworkIdentifier) &&
                    allConnectionsById[connection.ConnectionInfo.NetworkIdentifier].ContainsKey(connection.ConnectionInfo.ConnectionType) &&
                    allConnectionsById[connection.ConnectionInfo.NetworkIdentifier][connection.ConnectionInfo.ConnectionType].Contains(connection))
                    ||
                    (allConnectionsByEndPoint.ContainsKey(connection.ConnectionInfo.RemoteEndPoint) &&
                    allConnectionsByEndPoint[connection.ConnectionInfo.RemoteEndPoint].ContainsKey(connection.ConnectionInfo.ConnectionType)))
                {
                    //Maintain a reference if this is our first connection close
                    returnValue = true;
                }

                //Keep a reference of the connection for possible debugging later
                if (maintainConnectionInfoHistory)
                {
                    if (oldConnectionIdToConnectionInfo.ContainsKey(connection.ConnectionInfo.NetworkIdentifier))
                    {
                        if (oldConnectionIdToConnectionInfo[connection.ConnectionInfo.NetworkIdentifier].ContainsKey(connection.ConnectionInfo.ConnectionType))
                            oldConnectionIdToConnectionInfo[connection.ConnectionInfo.NetworkIdentifier][connection.ConnectionInfo.ConnectionType].Add(connection.ConnectionInfo);
                        else
                            oldConnectionIdToConnectionInfo[connection.ConnectionInfo.NetworkIdentifier].Add(connection.ConnectionInfo.ConnectionType, new List<ConnectionInfo>() { connection.ConnectionInfo });
                    }
                    else
                        oldConnectionIdToConnectionInfo.Add(connection.ConnectionInfo.NetworkIdentifier, new Dictionary<ConnectionType, List<ConnectionInfo>>() { { connection.ConnectionInfo.ConnectionType, new List<ConnectionInfo>() { connection.ConnectionInfo } } });
                }

                if (allConnectionsById.ContainsKey(connection.ConnectionInfo.NetworkIdentifier) &&
                        allConnectionsById[connection.ConnectionInfo.NetworkIdentifier].ContainsKey(connection.ConnectionInfo.ConnectionType))
                {
                    if (!allConnectionsById[connection.ConnectionInfo.NetworkIdentifier][connection.ConnectionInfo.ConnectionType].Contains(connection))
                        throw new ConnectionShutdownException("A reference to the connection being closed was not found in the allConnectionsById dictionary.");
                    else
                        allConnectionsById[connection.ConnectionInfo.NetworkIdentifier][connection.ConnectionInfo.ConnectionType].Remove(connection);
                }

                //We can now remove this connection by end point as well
                if (allConnectionsByEndPoint.ContainsKey(connection.ConnectionInfo.RemoteEndPoint))
                {
                    if (allConnectionsByEndPoint[connection.ConnectionInfo.RemoteEndPoint].ContainsKey(connection.ConnectionInfo.ConnectionType))
                        allConnectionsByEndPoint[connection.ConnectionInfo.RemoteEndPoint].Remove(connection.ConnectionInfo.ConnectionType);

                    //If this was the last connection type for this endpoint we can remove the endpoint reference as well
                    if (allConnectionsByEndPoint[connection.ConnectionInfo.RemoteEndPoint].Count == 0)
                        allConnectionsByEndPoint.Remove(connection.ConnectionInfo.RemoteEndPoint);
                }
                #endregion
            }

            return returnValue;
        }

        /// <summary>
        /// Adds a reference by IPEndPoint to the provided connection within networkComms.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="endPointToUse">An optional override which forces a specific IPEndPoint</param>
        internal static void AddConnectionByReferenceEndPoint(Connection connection, IPEndPoint endPointToUse = null)
        {
            //If the remoteEndPoint is IPAddress.Any we don't record it by endPoint
            if (connection.ConnectionInfo.RemoteEndPoint.Address.Equals(IPAddress.Any) || (endPointToUse != null && endPointToUse.Address.Equals(IPAddress.Any)))
                return;

            if (connection.ConnectionInfo.ConnectionEstablished || connection.ConnectionInfo.ConnectionShutdown)
                throw new ConnectionSetupException("Connection reference by endPoint should only be added before a connection is established. This is to prevent duplicate connections.");

            if (endPointToUse == null) endPointToUse = connection.ConnectionInfo.RemoteEndPoint;

            //How do we prevent multiple threads from trying to create a duplicate connection??
            lock (globalDictAndDelegateLocker)
            {
                if (ConnectionExists(endPointToUse, connection.ConnectionInfo.ConnectionType))
                {
                    if (RetrieveConnection(endPointToUse, connection.ConnectionInfo.ConnectionType) != connection)
                        throw new ConnectionSetupException("A different connection already exists with " + connection.ConnectionInfo);
                    else
                    {
                        //We have just tried to add the same reference twice, no need to do anything this time around
                    }
                }
                else
                {
                    //Add reference to the endPoint dictionary
                    if (allConnectionsByEndPoint.ContainsKey(endPointToUse))
                    {
                        if (allConnectionsByEndPoint[endPointToUse].ContainsKey(connection.ConnectionInfo.ConnectionType))
                            throw new Exception("Idiot check fail. The method ConnectionExists should have prevented execution getting here!!");
                        else
                            allConnectionsByEndPoint[endPointToUse].Add(connection.ConnectionInfo.ConnectionType, connection);
                    }
                    else
                        allConnectionsByEndPoint.Add(endPointToUse, new Dictionary<ConnectionType, Connection>() { { connection.ConnectionInfo.ConnectionType, connection } });
                }
            }
        }

        /// <summary>
        /// Update the endPoint reference for the provided connection with the newEndPoint. If there is no change just returns
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="newEndPoint"></param>
        internal static void UpdateConnectionReferenceByEndPoint(Connection connection, IPEndPoint newEndPoint)
        {
            if (!connection.ConnectionInfo.RemoteEndPoint.Equals(newEndPoint))
            {
                lock (globalDictAndDelegateLocker)
                {
                    RemoveConnectionReference(connection, false);
                    AddConnectionByReferenceEndPoint(connection, newEndPoint);
                }
            }
        }

        /// <summary>
        /// Add a reference by connectionId to the provided connection within NetworkComms. Requires a reference by IPEndPoint to already exist.
        /// </summary>
        /// <param name="connection"></param>
        internal static void AddConnectionReferenceByIdentifier(Connection connection)
        {
            if (!connection.ConnectionInfo.ConnectionEstablished || connection.ConnectionInfo.ConnectionShutdown)
                throw new ConnectionSetupException("Connection reference by identifier should only be added once a connection is established. This is to prevent duplicate connections.");

            if (connection.ConnectionInfo.NetworkIdentifier == ShortGuid.Empty)
                throw new ConnectionSetupException("Should not be calling AddConnectionByIdentifierReference unless the connection remote identifier has been set.");

            lock (globalDictAndDelegateLocker)
            {
                //There should already be a reference to this connection in the endPoint dictionary
                if (!ConnectionExists(connection.ConnectionInfo.RemoteEndPoint, connection.ConnectionInfo.ConnectionType))
                    throw new ConnectionSetupException("A reference by identifier should only be added if a reference by endPoint already exists.");

                //Check for an existing reference first, if there is one and it matches this connection then no worries
                if (allConnectionsById.ContainsKey(connection.ConnectionInfo.NetworkIdentifier))
                {
                    if (allConnectionsById[connection.ConnectionInfo.NetworkIdentifier].ContainsKey(connection.ConnectionInfo.ConnectionType))
                    {
                        if (!allConnectionsById[connection.ConnectionInfo.NetworkIdentifier][connection.ConnectionInfo.ConnectionType].Contains(connection))
                        {
                            if ((from current in allConnectionsById[connection.ConnectionInfo.NetworkIdentifier][connection.ConnectionInfo.ConnectionType]
                                 where current.ConnectionInfo.RemoteEndPoint.Equals(connection.ConnectionInfo.RemoteEndPoint)
                                 select current).Count() > 0)
                                throw new ConnectionSetupException("A different connection to the same remoteEndPoint already exists. Duplicate connections should be prevented elsewhere.");
                        }
                        else
                        {
                            //We are trying to add the same connection twice, so just do nothing here.
                        }
                    }
                    else
                        allConnectionsById[connection.ConnectionInfo.NetworkIdentifier].Add(connection.ConnectionInfo.ConnectionType, new List<Connection>() { connection });
                }
                else
                    allConnectionsById.Add(connection.ConnectionInfo.NetworkIdentifier, new Dictionary<ConnectionType, List<Connection>>() { { connection.ConnectionInfo.ConnectionType, new List<Connection>() {connection}} });
            }
        }
        #endregion

        #region Obsolete Send Receive Methods - These will be removed in the release after 2.0
        #region SendObjectDefault
        /// <summary>
        /// Send the provided object to the specified destination on the default comms port and sets the connectionId. Uses the network comms default compressor and serializer
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        /// <param name="connectionId">The connectionId used to complete the send. Can be used in subsequent sends without requiring ip address</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, string destinationIPAddress, bool receiveConfirmationRequired, object sendObject, ref ShortGuid connectionId)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            conn.SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified destination on a specific comms port and sets the connectionId. Uses the network comms default compressor and serializer
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="commsPort">The destination comms port</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        /// <param name="connectionId">The connectionId used to complete the send. Can be used in subsequent sends without requiring ip address</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, string destinationIPAddress, int commsPort, bool receiveConfirmationRequired, object sendObject, ref ShortGuid connectionId)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, commsPort));
            conn.SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
            connectionId = conn.ConnectionInfo.NetworkIdentifier;
        }

        /// <summary>
        /// Send the provided object to the specified destination on the default comms port. Uses the network comms default compressor and serializer
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, string destinationIPAddress, bool receiveConfirmationRequired, object sendObject)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            conn.SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified destination on a specific comms port. Uses the network comms default compressor and serializer
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="commsPort">The destination comms port</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, string destinationIPAddress, int commsPort, bool receiveConfirmationRequired, object sendObject)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, commsPort));
            conn.SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified connectionId. Uses the network comms default compressor and serializer
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="connectionId">Destination connection id</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, ShortGuid connectionId, bool receiveConfirmationRequired, object sendObject)
        {
            List<Connection> conns = RetrieveConnection(connectionId, ConnectionType.TCP);
            if (conns.Count == 0) throw new InvalidConnectionIdException("Unable to locate connection with provided connectionId.");
            conns[0].SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        #endregion SendObjectDefault
        #region SendObjectSpecific
        /// <summary>
        /// Send the provided object to the specified destination on the default comms port and sets the connectionId. Uses the provided compressor and serializer delegates
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        /// <param name="serializer">The specific serializer delegate to use</param>
        /// <param name="compressor">The specific compressor delegate to use</param>
        /// <param name="connectionId">The connectionId used to complete the send. Can be used in subsequent sends without requiring ip address</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, string destinationIPAddress, bool receiveConfirmationRequired, object sendObject, ISerialize serializer, ICompress compressor, ref ShortGuid connectionId)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            conn.SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializer, compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
            connectionId = conn.ConnectionInfo.NetworkIdentifier;
        }

        /// <summary>
        /// Send the provided object to the specified destination on a specific comms port and sets the connectionId. Uses the provided compressor and serializer delegates
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="commsPort">The destination comms port</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        /// <param name="serializer">The specific serializer delegate to use</param>
        /// <param name="compressor">The specific compressor delegate to use</param>
        /// <param name="connectionId">The connectionId used to complete the send. Can be used in subsequent sends without requiring ip address</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, string destinationIPAddress, int commsPort, bool receiveConfirmationRequired, object sendObject, ISerialize serializer, ICompress compressor, ref ShortGuid connectionId)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, commsPort));
            conn.SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializer, compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
            connectionId = conn.ConnectionInfo.NetworkIdentifier;
        }

        /// <summary>
        /// Send the provided object to the specified destination on the default comms port. Uses the provided compressor and serializer delegates
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        /// <param name="serializer">The specific serializer delegate to use</param>
        /// <param name="compressor">The specific compressor delegate to use</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, string destinationIPAddress, bool receiveConfirmationRequired, object sendObject, ISerialize serializer, ICompress compressor)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            conn.SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializer, compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified destination on a specific comms port. Uses the provided compressor and serializer delegates
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="commsPort">The destination comms port</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        /// <param name="serializer">The specific serializer delegate to use</param>
        /// <param name="compressor">The specific compressor delegate to use</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, string destinationIPAddress, int commsPort, bool receiveConfirmationRequired, object sendObject, ISerialize serializer, ICompress compressor)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, commsPort));
            conn.SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializer, compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified connectionId. Uses the provided compressor and serializer delegates
        /// </summary>
        /// <param name="packetTypeStr">Packet type to use during send</param>
        /// <param name="connectionId">Destination connection id</param>
        /// <param name="receiveConfirmationRequired">If true will return only when object is successfully received at destination</param>
        /// <param name="sendObject">The obect to send</param>
        /// <param name="serializer">The specific serializer delegate to use</param>
        /// <param name="compressor">The specific compressor delegate to use</param>
        [Obsolete]
        public static void SendObject(string packetTypeStr, ShortGuid connectionId, bool receiveConfirmationRequired, object sendObject, ISerialize serializer, ICompress compressor)
        {
            List<Connection> conns = RetrieveConnection(connectionId, ConnectionType.TCP);
            if (conns.Count == 0) throw new InvalidConnectionIdException("Unable to locate connection with provided connectionId.");
            conns[0].SendObject(packetTypeStr, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializer, compressor, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }
        #endregion

        #region SendReceiveObjectDefault
        /// <summary>
        /// Send the provided object to the specified destination on the default comms port, setting the connectionId, and wait for the return object. Uses the network comms default compressor and serializer
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <param name="connectionId">The connectionId used to complete the send. Can be used in subsequent sends without requiring ip address</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject, ref ShortGuid connectionId)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            connectionId = conn.ConnectionInfo.NetworkIdentifier;
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority), DefaultSendReceiveOptions);
        }

        /// <summary>
        /// Send the provided object to the specified destination on a specific comms port, setting the connectionId, and wait for the return object. Uses the network comms default compressor and serializer
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="commsPort">The destination comms port</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <param name="connectionId">The connectionId used to complete the send. Can be used in subsequent sends without requiring ip address</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, int commsPort, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject, ref ShortGuid connectionId)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, commsPort));
            connectionId = conn.ConnectionInfo.NetworkIdentifier;
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority), DefaultSendReceiveOptions);
        }

        /// <summary>
        /// Send the provided object to the specified destination on the default comms port and wait for the return object. Uses the network comms default compressor and serializer
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority), DefaultSendReceiveOptions);
        }

        /// <summary>
        /// Send the provided object to the specified destination on a specific comms port and wait for the return object. Uses the network comms default compressor and serializer
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="commsPort">The destination comms port</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, int commsPort, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, commsPort));
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority), DefaultSendReceiveOptions);
        }

        /// <summary>
        /// Send the provided object to the specified connectionId and wait for the return object. Uses the network comms default compressor and serializer
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="connectionId">Destination connection id</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, ShortGuid connectionId, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject)
        {
            List<Connection> conns = RetrieveConnection(connectionId, ConnectionType.TCP);
            if (conns.Count == 0) throw new InvalidConnectionIdException("Unable to locate connection with provided connectionId.");
            return conns[0].SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, DefaultSendReceiveOptions.Serializer, DefaultSendReceiveOptions.Compressor, DefaultSendReceiveOptions.ReceiveHandlePriority), DefaultSendReceiveOptions);
        }

        #endregion SendReceiveObjectDefault
        #region SendReceiveObjectSpecific
        /// <summary>
        /// Send the provided object to the specified destination on the default comms port, setting the connectionId, and wait for the return object. Uses the provided compressors and serializers
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <param name="serializerOutgoing">Serializer to use for outgoing object</param>
        /// <param name="compressorOutgoing">Compressor to use for outgoing object</param>
        /// <param name="serializerIncoming">Serializer to use for return object</param>
        /// <param name="compressorIncoming">Compressor to use for return object</param>
        /// <param name="connectionId">The connectionId used to complete the send. Can be used in subsequent sends without requiring ip address</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject, ISerialize serializerOutgoing, ICompress compressorOutgoing, ISerialize serializerIncoming, ICompress compressorIncoming, ref ShortGuid connectionId)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            connectionId = conn.ConnectionInfo.NetworkIdentifier;
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializerOutgoing, compressorOutgoing, DefaultSendReceiveOptions.ReceiveHandlePriority), new SendReceiveOptions(false, serializerIncoming, compressorIncoming, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified destination on a specific comms port, setting the connectionId, and wait for the return object. Uses the provided compressors and serializers
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="commsPort">The destination comms port</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <param name="serializerOutgoing">Serializer to use for outgoing object</param>
        /// <param name="compressorOutgoing">Compressor to use for outgoing object</param>
        /// <param name="serializerIncoming">Serializer to use for return object</param>
        /// <param name="compressorIncoming">Compressor to use for return object</param>
        /// <param name="connectionId">The connectionId used to complete the send. Can be used in subsequent sends without requiring ip address</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, int commsPort, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject, ISerialize serializerOutgoing, ICompress compressorOutgoing, ISerialize serializerIncoming, ICompress compressorIncoming, ref ShortGuid connectionId)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, commsPort));
            connectionId = conn.ConnectionInfo.NetworkIdentifier;
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializerOutgoing, compressorOutgoing, DefaultSendReceiveOptions.ReceiveHandlePriority), new SendReceiveOptions(false, serializerIncoming, compressorIncoming, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified destination on the default comms port and wait for the return object. Uses the provided compressors and serializers
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <param name="serializerOutgoing">Serializer to use for outgoing object</param>
        /// <param name="compressorOutgoing">Compressor to use for outgoing object</param>
        /// <param name="serializerIncoming">Serializer to use for return object</param>
        /// <param name="compressorIncoming">Compressor to use for return object</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject, ISerialize serializerOutgoing, ICompress compressorOutgoing, ISerialize serializerIncoming, ICompress compressorIncoming)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, DefaultListenPort));
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializerOutgoing, compressorOutgoing, DefaultSendReceiveOptions.ReceiveHandlePriority), new SendReceiveOptions(false, serializerIncoming, compressorIncoming, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified destination on a specific comms port and wait for the return object. Uses the provided compressors and serializers
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="destinationIPAddress">The destination ip address</param>
        /// <param name="commsPort">The destination comms port</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <param name="serializerOutgoing">Serializer to use for outgoing object</param>
        /// <param name="compressorOutgoing">Compressor to use for outgoing object</param>
        /// <param name="serializerIncoming">Serializer to use for return object</param>
        /// <param name="compressorIncoming">Compressor to use for return object</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, string destinationIPAddress, int commsPort, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject, ISerialize serializerOutgoing, ICompress compressorOutgoing, ISerialize serializerIncoming, ICompress compressorIncoming)
        {
            TCPConnection conn = TCPConnection.CreateConnection(new ConnectionInfo(destinationIPAddress, commsPort));
            return conn.SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializerOutgoing, compressorOutgoing, DefaultSendReceiveOptions.ReceiveHandlePriority), new SendReceiveOptions(false, serializerIncoming, compressorIncoming, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }

        /// <summary>
        /// Send the provided object to the specified connectionId and wait for the return object. Uses the provided compressors and serializers
        /// </summary>
        /// <typeparam name="returnObjectType">The expected return object type, i.e. string, int[], etc</typeparam>
        /// <param name="sendingPacketTypeStr">Packet type to use during send</param>
        /// <param name="connectionId">Destination connection id</param>
        /// <param name="receiveConfirmationRequired">If true will throw an exception if object is not received at destination within PacketConfirmationTimeoutMS timeout. This may be significantly less than the provided returnPacketTimeOutMilliSeconds.</param>
        /// <param name="expectedReturnPacketTypeStr">Expected packet type used for return object</param>
        /// <param name="returnPacketTimeOutMilliSeconds">Time to wait in milliseconds for return object</param>
        /// <param name="sendObject">Object to send</param>
        /// <param name="serializerOutgoing">Serializer to use for outgoing object</param>
        /// <param name="compressorOutgoing">Compressor to use for outgoing object</param>
        /// <param name="serializerIncoming">Serializer to use for return object</param>
        /// <param name="compressorIncoming">Compressor to use for return object</param>
        /// <returns>The expected return object</returns>
        [Obsolete]
        public static returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, ShortGuid connectionId, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject, ISerialize serializerOutgoing, ICompress compressorOutgoing, ISerialize serializerIncoming, ICompress compressorIncoming)
        {
            List<Connection> conns = RetrieveConnection(connectionId, ConnectionType.TCP);
            if (conns.Count == 0) throw new InvalidConnectionIdException("Unable to locate connection with provided connectionId.");
            return conns[0].SendReceiveObject<returnObjectType>(sendingPacketTypeStr, expectedReturnPacketTypeStr, returnPacketTimeOutMilliSeconds, sendObject, new SendReceiveOptions(receiveConfirmationRequired, serializerOutgoing, compressorOutgoing, DefaultSendReceiveOptions.ReceiveHandlePriority), new SendReceiveOptions(false, serializerIncoming, compressorIncoming, DefaultSendReceiveOptions.ReceiveHandlePriority));
        }
        #endregion SendReceiveObjectSpecific
        #endregion
    }
}