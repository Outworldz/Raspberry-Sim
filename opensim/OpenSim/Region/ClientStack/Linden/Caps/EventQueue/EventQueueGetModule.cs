/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenMetaverse.Messages.Linden;
using OpenMetaverse.Packets;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using BlockingLLSDQueue = OpenSim.Framework.BlockingQueue<OpenMetaverse.StructuredData.OSD>;
using Caps=OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    public struct QueueItem
    {
        public int id;
        public OSDMap body;
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EventQueueGetModule")]
    public class EventQueueGetModule : IEventQueue, INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static string LogHeader = "[EVENT QUEUE GET MODULE]";

        /// <value>
        /// Debug level.
        /// </value>
        public int DebugLevel { get; set; }

        // Viewer post requests timeout in 60 secs
        // https://bitbucket.org/lindenlab/viewer-release/src/421c20423df93d650cc305dc115922bb30040999/indra/llmessage/llhttpclient.cpp?at=default#cl-44
        //
        private const int VIEWER_TIMEOUT = 60 * 1000;
        // Just to be safe, we work on a 10 sec shorter cycle
        private const int SERVER_EQ_TIME_NO_EVENTS = VIEWER_TIMEOUT - (10 * 1000);

        protected Scene m_scene;
        
        private Dictionary<UUID, int> m_ids = new Dictionary<UUID, int>();

        private Dictionary<UUID, Queue<OSD>> queues = new Dictionary<UUID, Queue<OSD>>();
        private Dictionary<UUID, UUID> m_QueueUUIDAvatarMapping = new Dictionary<UUID, UUID>();
        private Dictionary<UUID, UUID> m_AvatarQueueUUIDMapping = new Dictionary<UUID, UUID>();
            
        #region INonSharedRegionModule methods
        public virtual void Initialise(IConfigSource config)
        {
        }

        public void AddRegion(Scene scene)
        {
            m_scene = scene;
            scene.RegisterModuleInterface<IEventQueue>(this);

            scene.EventManager.OnClientClosed += ClientClosed;
            scene.EventManager.OnRegisterCaps += OnRegisterCaps;

            MainConsole.Instance.Commands.AddCommand(
                "Debug",
                false,
                "debug eq",
                "debug eq [0|1|2]",
                "Turn on event queue debugging\n"
                    + "  <= 0 - turns off all event queue logging\n"
                    + "  >= 1 - turns on event queue setup and outgoing event logging\n"
                    + "  >= 2 - turns on poll notification",
                HandleDebugEq);

            MainConsole.Instance.Commands.AddCommand(
                "Debug",
                false,
                "show eq",
                "show eq",
                "Show contents of event queues for logged in avatars.  Used for debugging.",
                HandleShowEq);
        }

        public void RemoveRegion(Scene scene)
        {
            if (m_scene != scene)
                return;

            scene.EventManager.OnClientClosed -= ClientClosed;
            scene.EventManager.OnRegisterCaps -= OnRegisterCaps;

            scene.UnregisterModuleInterface<IEventQueue>(this);
            m_scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public virtual void Close()
        {
        }

        public virtual string Name
        {
            get { return "EventQueueGetModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        protected void HandleDebugEq(string module, string[] args)
        {
            int debugLevel;

            if (!(args.Length == 3 && int.TryParse(args[2], out debugLevel)))
            {
                MainConsole.Instance.OutputFormat("Usage: debug eq [0|1|2]");
            }
            else
            {
                DebugLevel = debugLevel;
                MainConsole.Instance.OutputFormat(
                    "Set event queue debug level to {0} in {1}", DebugLevel, m_scene.RegionInfo.RegionName);
            }
        }

        protected void HandleShowEq(string module, string[] args)
        {
            MainConsole.Instance.OutputFormat("For scene {0}", m_scene.Name);

            lock (queues)
            {
                foreach (KeyValuePair<UUID, Queue<OSD>> kvp in queues)
                {
                    MainConsole.Instance.OutputFormat(
                        "For agent {0} there are {1} messages queued for send.", 
                        kvp.Key, kvp.Value.Count);
                }
            }
        }

        /// <summary>
        ///  Always returns a valid queue
        /// </summary>
        /// <param name="agentId"></param>
        /// <returns></returns>
        private Queue<OSD> TryGetQueue(UUID agentId)
        {
            lock (queues)
            {
                if (!queues.ContainsKey(agentId))
                {
                    if (DebugLevel > 0)
                        m_log.DebugFormat(
                            "[EVENTQUEUE]: Adding new queue for agent {0} in region {1}", 
                            agentId, m_scene.RegionInfo.RegionName);

                    queues[agentId] = new Queue<OSD>();
                }

                return queues[agentId];
            }
        }

        /// <summary>
        /// May return a null queue
        /// </summary>
        /// <param name="agentId"></param>
        /// <returns></returns>
        private Queue<OSD> GetQueue(UUID agentId)
        {
            lock (queues)
            {
                if (queues.ContainsKey(agentId))
                {
                    return queues[agentId];
                }
                else
                    return null;
            }
        }

        #region IEventQueue Members

        public bool Enqueue(OSD ev, UUID avatarID)
        {
            //m_log.DebugFormat("[EVENTQUEUE]: Enqueuing event for {0} in region {1}", avatarID, m_scene.RegionInfo.RegionName);
            try
            {
                Queue<OSD> queue = GetQueue(avatarID);
                if (queue != null)
                {
                    lock (queue)
                        queue.Enqueue(ev);
                }
                else if (DebugLevel > 0)
                {
                    ScenePresence sp = m_scene.GetScenePresence(avatarID);

                    // This assumes that an NPC should never have a queue.
                    if (sp != null && sp.PresenceType != PresenceType.Npc)
                    {
                        OSDMap evMap = (OSDMap)ev;
                        m_log.WarnFormat(
                            "[EVENTQUEUE]: (Enqueue) No queue found for agent {0} {1} when placing message {2} in region {3}", 
                            sp.Name, sp.UUID, evMap["message"], m_scene.Name);
                    }
                }
            } 
            catch (NullReferenceException e)
            {
                m_log.Error("[EVENTQUEUE] Caught exception: " + e);
                return false;
            }
            
            return true;
        }

        #endregion

        private void ClientClosed(UUID agentID, Scene scene)
        {
            //m_log.DebugFormat("[EVENTQUEUE]: Closed client {0} in region {1}", agentID, m_scene.RegionInfo.RegionName);

            lock (queues)
                queues.Remove(agentID);

            List<UUID> removeitems = new List<UUID>();
            lock (m_AvatarQueueUUIDMapping)
                m_AvatarQueueUUIDMapping.Remove(agentID);

            UUID searchval = UUID.Zero;

            removeitems.Clear();
            
            lock (m_QueueUUIDAvatarMapping)
            {
                foreach (UUID ky in m_QueueUUIDAvatarMapping.Keys)
                {
                    searchval = m_QueueUUIDAvatarMapping[ky];

                    if (searchval == agentID)
                    {
                        removeitems.Add(ky);
                    }
                }

                foreach (UUID ky in removeitems)
                    m_QueueUUIDAvatarMapping.Remove(ky);
            }

            // m_log.DebugFormat("[EVENTQUEUE]: Deleted queues for {0} in region {1}", agentID, m_scene.RegionInfo.RegionName);

        }

        /// <summary>
        /// Generate an Event Queue Get handler path for the given eqg uuid.
        /// </summary>
        /// <param name='eqgUuid'></param>
        private string GenerateEqgCapPath(UUID eqgUuid)
        {
            return string.Format("/CAPS/EQG/{0}/", eqgUuid);
        }

        public void OnRegisterCaps(UUID agentID, Caps caps)
        {
            // Register an event queue for the client

            if (DebugLevel > 0)
                m_log.DebugFormat(
                    "[EVENTQUEUE]: OnRegisterCaps: agentID {0} caps {1} region {2}",
                    agentID, caps, m_scene.RegionInfo.RegionName);

            // Let's instantiate a Queue for this agent right now
            TryGetQueue(agentID);

            UUID eventQueueGetUUID;

            lock (m_AvatarQueueUUIDMapping)
            {
                // Reuse open queues.  The client does!
                if (m_AvatarQueueUUIDMapping.ContainsKey(agentID))
                {
                    //m_log.DebugFormat("[EVENTQUEUE]: Found Existing UUID!");
                    eventQueueGetUUID = m_AvatarQueueUUIDMapping[agentID];
                }
                else
                {
                    eventQueueGetUUID = UUID.Random();
                    //m_log.DebugFormat("[EVENTQUEUE]: Using random UUID!");
                }
            }

            lock (m_QueueUUIDAvatarMapping)
            {
                if (!m_QueueUUIDAvatarMapping.ContainsKey(eventQueueGetUUID))
                    m_QueueUUIDAvatarMapping.Add(eventQueueGetUUID, agentID);
            }

            lock (m_AvatarQueueUUIDMapping)
            {
                if (!m_AvatarQueueUUIDMapping.ContainsKey(agentID))
                    m_AvatarQueueUUIDMapping.Add(agentID, eventQueueGetUUID);
            }

            caps.RegisterPollHandler(
                "EventQueueGet",
                new PollServiceEventArgs(null, GenerateEqgCapPath(eventQueueGetUUID), HasEvents, GetEvents, NoEvents, agentID, SERVER_EQ_TIME_NO_EVENTS));

            Random rnd = new Random(Environment.TickCount);
            lock (m_ids)
            {
                if (!m_ids.ContainsKey(agentID))
                    m_ids.Add(agentID, rnd.Next(30000000));
            }
        }

        public bool HasEvents(UUID requestID, UUID agentID)
        {
            // Don't use this, because of race conditions at agent closing time
            //Queue<OSD> queue = TryGetQueue(agentID);

            Queue<OSD> queue = GetQueue(agentID);
            if (queue != null)
                lock (queue)
                {
                    //m_log.WarnFormat("POLLED FOR EVENTS BY {0} in {1} -- {2}", agentID, m_scene.RegionInfo.RegionName, queue.Count);
                    return queue.Count > 0;
                }

            return false;
        }

        /// <summary>
        /// Logs a debug line for an outbound event queue message if appropriate.
        /// </summary>
        /// <param name='element'>Element containing message</param>
        private void LogOutboundDebugMessage(OSD element, UUID agentId)
        {
            if (element is OSDMap)
            {
                OSDMap ev = (OSDMap)element;
                m_log.DebugFormat(
                    "Eq OUT {0,-30} to {1,-20} {2,-20}",
                    ev["message"], m_scene.GetScenePresence(agentId).Name, m_scene.Name);
            }
        }

        public Hashtable GetEvents(UUID requestID, UUID pAgentId)
        {
            if (DebugLevel >= 2)
                m_log.WarnFormat("POLLED FOR EQ MESSAGES BY {0} in {1}", pAgentId, m_scene.Name);

            Queue<OSD> queue = GetQueue(pAgentId);
            if (queue == null)
            {
                return NoEvents(requestID, pAgentId);
            }

            OSD element;
            lock (queue)
            {
                if (queue.Count == 0)
                    return NoEvents(requestID, pAgentId);
                element = queue.Dequeue(); // 15s timeout
            }

            int thisID = 0;
            lock (m_ids)
                thisID = m_ids[pAgentId];

            OSDArray array = new OSDArray();
            if (element == null) // didn't have an event in 15s
            {
                // Send it a fake event to keep the client polling!   It doesn't like 502s like the proxys say!
                array.Add(EventQueueHelper.KeepAliveEvent());
                //m_log.DebugFormat("[EVENTQUEUE]: adding fake event for {0} in region {1}", pAgentId, m_scene.RegionInfo.RegionName);
            }
            else
            {
                if (DebugLevel > 0)
                    LogOutboundDebugMessage(element, pAgentId);

                array.Add(element);

                lock (queue)
                {
                    while (queue.Count > 0)
                    {
                        element = queue.Dequeue();

                        if (DebugLevel > 0)
                            LogOutboundDebugMessage(element, pAgentId);

                        array.Add(element);
                        thisID++;
                    }
                }
            }

            OSDMap events = new OSDMap();
            events.Add("events", array);

            events.Add("id", new OSDInteger(thisID));
            lock (m_ids)
            {
                m_ids[pAgentId] = thisID + 1;
            }
            Hashtable responsedata = new Hashtable();
            responsedata["int_response_code"] = 200;
            responsedata["content_type"] = "application/xml";
            responsedata["keepalive"] = false;
            responsedata["reusecontext"] = false;
            responsedata["str_response_string"] = OSDParser.SerializeLLSDXmlString(events);
            //m_log.DebugFormat("[EVENTQUEUE]: sending response for {0} in region {1}: {2}", pAgentId, m_scene.RegionInfo.RegionName, responsedata["str_response_string"]);
            return responsedata;
        }

        public Hashtable NoEvents(UUID requestID, UUID agentID)
        {
            Hashtable responsedata = new Hashtable();
            responsedata["int_response_code"] = 502;
            responsedata["content_type"] = "text/plain";
            responsedata["keepalive"] = false;
            responsedata["reusecontext"] = false;
            responsedata["str_response_string"] = "Upstream error: ";
            responsedata["error_status_text"] = "Upstream error:";
            responsedata["http_protocol_version"] = "HTTP/1.0";
            return responsedata;
        }

//        public Hashtable ProcessQueue(Hashtable request, UUID agentID, Caps caps)
//        {
//            // TODO: this has to be redone to not busy-wait (and block the thread),
//            // TODO: as soon as we have a non-blocking way to handle HTTP-requests.
//
////            if (m_log.IsDebugEnabled)
////            {
////                String debug = "[EVENTQUEUE]: Got request for agent {0} in region {1} from thread {2}: [  ";
////                foreach (object key in request.Keys)
////                {
////                    debug += key.ToString() + "=" + request[key].ToString() + "  ";
////                }
////                m_log.DebugFormat(debug + "  ]", agentID, m_scene.RegionInfo.RegionName, System.Threading.Thread.CurrentThread.Name);
////            }
//
//            Queue<OSD> queue = TryGetQueue(agentID);
//            OSD element;
//
//            lock (queue)
//                element = queue.Dequeue(); // 15s timeout
//
//            Hashtable responsedata = new Hashtable();
//
//            int thisID = 0;
//            lock (m_ids)
//                thisID = m_ids[agentID];
//
//            if (element == null)
//            {
//                //m_log.ErrorFormat("[EVENTQUEUE]: Nothing to process in " + m_scene.RegionInfo.RegionName);
//                if (thisID == -1) // close-request
//                {
//                    m_log.ErrorFormat("[EVENTQUEUE]: 404 in " + m_scene.RegionInfo.RegionName);
//                    responsedata["int_response_code"] = 404; //501; //410; //404;
//                    responsedata["content_type"] = "text/plain";
//                    responsedata["keepalive"] = false;
//                    responsedata["str_response_string"] = "Closed EQG";
//                    return responsedata;
//                }
//                responsedata["int_response_code"] = 502;
//                responsedata["content_type"] = "text/plain";
//                responsedata["keepalive"] = false;
//                responsedata["str_response_string"] = "Upstream error: ";
//                responsedata["error_status_text"] = "Upstream error:";
//                responsedata["http_protocol_version"] = "HTTP/1.0";
//                return responsedata;
//            }
//
//            OSDArray array = new OSDArray();
//            if (element == null) // didn't have an event in 15s
//            {
//                // Send it a fake event to keep the client polling!   It doesn't like 502s like the proxys say!
//                array.Add(EventQueueHelper.KeepAliveEvent());
//                //m_log.DebugFormat("[EVENTQUEUE]: adding fake event for {0} in region {1}", agentID, m_scene.RegionInfo.RegionName);
//            }
//            else
//            {
//                array.Add(element);
//
//                if (element is OSDMap)
//                {
//                    OSDMap ev = (OSDMap)element;
//                    m_log.DebugFormat(
//                        "[EVENT QUEUE GET MODULE]: Eq OUT {0} to {1}",
//                        ev["message"], m_scene.GetScenePresence(agentID).Name);
//                }
//
//                lock (queue)
//                {
//                    while (queue.Count > 0)
//                    {
//                        element = queue.Dequeue();
//
//                        if (element is OSDMap)
//                        {
//                            OSDMap ev = (OSDMap)element;
//                            m_log.DebugFormat(
//                                "[EVENT QUEUE GET MODULE]: Eq OUT {0} to {1}",
//                                ev["message"], m_scene.GetScenePresence(agentID).Name);
//                        }
//
//                        array.Add(element);
//                        thisID++;
//                    }
//                }
//            }
//
//            OSDMap events = new OSDMap();
//            events.Add("events", array);
//
//            events.Add("id", new OSDInteger(thisID));
//            lock (m_ids)
//            {
//                m_ids[agentID] = thisID + 1;
//            }
//
//            responsedata["int_response_code"] = 200;
//            responsedata["content_type"] = "application/xml";
//            responsedata["keepalive"] = false;
//            responsedata["str_response_string"] = OSDParser.SerializeLLSDXmlString(events);
//
//            m_log.DebugFormat("[EVENTQUEUE]: sending response for {0} in region {1}: {2}", agentID, m_scene.RegionInfo.RegionName, responsedata["str_response_string"]);
//
//            return responsedata;
//        }

//        public Hashtable EventQueuePath2(Hashtable request)
//        {
//            string capuuid = (string)request["uri"]; //path.Replace("/CAPS/EQG/","");
//            // pull off the last "/" in the path.
//            Hashtable responsedata = new Hashtable();
//            capuuid = capuuid.Substring(0, capuuid.Length - 1);
//            capuuid = capuuid.Replace("/CAPS/EQG/", "");
//            UUID AvatarID = UUID.Zero;
//            UUID capUUID = UUID.Zero;
//
//            // parse the path and search for the avatar with it registered
//            if (UUID.TryParse(capuuid, out capUUID))
//            {
//                lock (m_QueueUUIDAvatarMapping)
//                {
//                    if (m_QueueUUIDAvatarMapping.ContainsKey(capUUID))
//                    {
//                        AvatarID = m_QueueUUIDAvatarMapping[capUUID];
//                    }
//                }
//                
//                if (AvatarID != UUID.Zero)
//                {
//                    return ProcessQueue(request, AvatarID, m_scene.CapsModule.GetCapsForUser(AvatarID));
//                }
//                else
//                {
//                    responsedata["int_response_code"] = 404;
//                    responsedata["content_type"] = "text/plain";
//                    responsedata["keepalive"] = false;
//                    responsedata["str_response_string"] = "Not Found";
//                    responsedata["error_status_text"] = "Not Found";
//                    responsedata["http_protocol_version"] = "HTTP/1.0";
//                    return responsedata;
//                    // return 404
//                }
//            }
//            else
//            {
//                responsedata["int_response_code"] = 404;
//                responsedata["content_type"] = "text/plain";
//                responsedata["keepalive"] = false;
//                responsedata["str_response_string"] = "Not Found";
//                responsedata["error_status_text"] = "Not Found";
//                responsedata["http_protocol_version"] = "HTTP/1.0";
//                return responsedata;
//                // return 404
//            }
//        }

        public OSD EventQueueFallBack(string path, OSD request, string endpoint)
        {
            // This is a fallback element to keep the client from loosing EventQueueGet
            // Why does CAPS fail sometimes!?
            m_log.Warn("[EVENTQUEUE]: In the Fallback handler!   We lost the Queue in the rest handler!");
            string capuuid = path.Replace("/CAPS/EQG/","");
            capuuid = capuuid.Substring(0, capuuid.Length - 1);

//            UUID AvatarID = UUID.Zero;
            UUID capUUID = UUID.Zero;
            if (UUID.TryParse(capuuid, out capUUID))
            {
/* Don't remove this yet code cleaners!
 * Still testing this!
 * 
                lock (m_QueueUUIDAvatarMapping)
                {
                    if (m_QueueUUIDAvatarMapping.ContainsKey(capUUID))
                    {
                        AvatarID = m_QueueUUIDAvatarMapping[capUUID];
                    }
                }
                
                 
                if (AvatarID != UUID.Zero)
                {
                    // Repair the CAP!
                    //OpenSim.Framework.Capabilities.Caps caps = m_scene.GetCapsHandlerForUser(AvatarID);
                    //string capsBase = "/CAPS/EQG/";
                    //caps.RegisterHandler("EventQueueGet",
                                //new RestHTTPHandler("POST", capsBase + capUUID.ToString() + "/",
                                                      //delegate(Hashtable m_dhttpMethod)
                                                      //{
                                                      //    return ProcessQueue(m_dhttpMethod, AvatarID, caps);
                                                      //}));
                    // start new ID sequence.
                    Random rnd = new Random(System.Environment.TickCount);
                    lock (m_ids)
                    {
                        if (!m_ids.ContainsKey(AvatarID))
                            m_ids.Add(AvatarID, rnd.Next(30000000));
                    }


                    int thisID = 0;
                    lock (m_ids)
                        thisID = m_ids[AvatarID];

                    BlockingLLSDQueue queue = GetQueue(AvatarID);
                    OSDArray array = new OSDArray();
                    LLSD element = queue.Dequeue(15000); // 15s timeout
                    if (element == null)
                    {
                        
                        array.Add(EventQueueHelper.KeepAliveEvent());
                    }
                    else
                    {
                        array.Add(element);
                        while (queue.Count() > 0)
                        {
                            array.Add(queue.Dequeue(1));
                            thisID++;
                        }
                    }
                    OSDMap events = new OSDMap();
                    events.Add("events", array);

                    events.Add("id", new LLSDInteger(thisID));
                    
                    lock (m_ids)
                    {
                        m_ids[AvatarID] = thisID + 1;
                    }
                    
                    return events;
                }
                else
                {
                    return new LLSD();
                }
* 
*/
            }
            else
            {
                //return new LLSD();
            }
            
            return new OSDString("shutdown404!");
        }

        public void DisableSimulator(ulong handle, UUID avatarID)
        {
            OSD item = EventQueueHelper.DisableSimulator(handle);
            Enqueue(item, avatarID);
        }

        public virtual void EnableSimulator(ulong handle, IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY)
        {
            if (DebugLevel > 0)
                m_log.DebugFormat("{0} EnableSimulator. handle={1}, endPoint={2}, avatarID={3}",
                    LogHeader, handle, endPoint, avatarID, regionSizeX, regionSizeY);

            OSD item = EventQueueHelper.EnableSimulator(handle, endPoint, regionSizeX, regionSizeY);
            Enqueue(item, avatarID);
        }

        public virtual void EstablishAgentCommunication(UUID avatarID, IPEndPoint endPoint, string capsPath,
                                ulong regionHandle, int regionSizeX, int regionSizeY) 
        {
            if (DebugLevel > 0)
                m_log.DebugFormat("{0} EstablishAgentCommunication. handle={1}, endPoint={2}, avatarID={3}",
                    LogHeader, regionHandle, endPoint, avatarID, regionSizeX, regionSizeY);

            OSD item = EventQueueHelper.EstablishAgentCommunication(avatarID, endPoint.ToString(), capsPath, regionHandle, regionSizeX, regionSizeY);
            Enqueue(item, avatarID);
        }

        public virtual void TeleportFinishEvent(ulong regionHandle, byte simAccess, 
                                        IPEndPoint regionExternalEndPoint,
                                        uint locationID, uint flags, string capsURL, 
                                        UUID avatarID, int regionSizeX, int regionSizeY)
        {
            if (DebugLevel > 0)
                m_log.DebugFormat("{0} TeleportFinishEvent. handle={1}, endPoint={2}, avatarID={3}",
                    LogHeader, regionHandle, regionExternalEndPoint, avatarID, regionSizeX, regionSizeY);

            OSD item = EventQueueHelper.TeleportFinishEvent(regionHandle, simAccess, regionExternalEndPoint,
                                                            locationID, flags, capsURL, avatarID, regionSizeX, regionSizeY);
            Enqueue(item, avatarID);
        }

        public virtual void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt,
                                IPEndPoint newRegionExternalEndPoint,
                                string capsURL, UUID avatarID, UUID sessionID, int regionSizeX, int regionSizeY)
        {
            if (DebugLevel > 0)
                m_log.DebugFormat("{0} CrossRegion. handle={1}, avatarID={2}, regionSize={3},{4}>",
                    LogHeader, handle, avatarID, regionSizeX, regionSizeY);

            OSD item = EventQueueHelper.CrossRegion(handle, pos, lookAt, newRegionExternalEndPoint,
                                                    capsURL, avatarID, sessionID, regionSizeX, regionSizeY);
            Enqueue(item, avatarID);
        }

        public void ChatterboxInvitation(UUID sessionID, string sessionName,
                                         UUID fromAgent, string message, UUID toAgent, string fromName, byte dialog,
                                         uint timeStamp, bool offline, int parentEstateID, Vector3 position,
                                         uint ttl, UUID transactionID, bool fromGroup, byte[] binaryBucket)
        {
            OSD item = EventQueueHelper.ChatterboxInvitation(sessionID, sessionName, fromAgent, message, toAgent, fromName, dialog, 
                                                             timeStamp, offline, parentEstateID, position, ttl, transactionID, 
                                                             fromGroup, binaryBucket);
            Enqueue(item, toAgent);
            //m_log.InfoFormat("########### eq ChatterboxInvitation #############\n{0}", item);

        }

        public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID fromAgent, UUID anotherAgent, bool canVoiceChat, 
                                                      bool isModerator, bool textMute)
        {
            OSD item = EventQueueHelper.ChatterBoxSessionAgentListUpdates(sessionID, fromAgent, canVoiceChat,
                                                                          isModerator, textMute);
            Enqueue(item, fromAgent);
            //m_log.InfoFormat("########### eq ChatterBoxSessionAgentListUpdates #############\n{0}", item);
        }

        public void ParcelProperties(ParcelPropertiesMessage parcelPropertiesMessage, UUID avatarID)
        {
            OSD item = EventQueueHelper.ParcelProperties(parcelPropertiesMessage);
            Enqueue(item, avatarID);
        }

        public void GroupMembership(AgentGroupDataUpdatePacket groupUpdate, UUID avatarID)
        {
            OSD item = EventQueueHelper.GroupMembership(groupUpdate);
            Enqueue(item, avatarID);
        }

        public void QueryReply(PlacesReplyPacket groupUpdate, UUID avatarID)
        {
            OSD item = EventQueueHelper.PlacesQuery(groupUpdate);
            Enqueue(item, avatarID);
        }

        public OSD ScriptRunningEvent(UUID objectID, UUID itemID, bool running, bool mono)
        {
            return EventQueueHelper.ScriptRunningReplyEvent(objectID, itemID, running, mono);
        }

        public OSD BuildEvent(string eventName, OSD eventBody)
        {
            return EventQueueHelper.BuildEvent(eventName, eventBody);
        }

        public void partPhysicsProperties(uint localID, byte physhapetype,
                        float density, float friction, float bounce, float gravmod,UUID avatarID)
        {
            OSD item = EventQueueHelper.partPhysicsProperties(localID, physhapetype,
                        density, friction, bounce, gravmod);
            Enqueue(item, avatarID);
        }
    }
}