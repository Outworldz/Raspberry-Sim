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
using System.Collections.Generic;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.Packets;
using log4net;
using OpenSim.Framework.Monitoring;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    public sealed class PacketPool
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly PacketPool instance = new PacketPool();

        /// <summary>
        /// Pool of packets available for reuse.
        /// </summary>
        private readonly Dictionary<PacketType, Stack<Packet>> pool = new Dictionary<PacketType, Stack<Packet>>();

        private static Dictionary<Type, Stack<Object>> DataBlocks = new Dictionary<Type, Stack<Object>>();

        public static PacketPool Instance
        {
            get { return instance; }
        }

        public bool RecyclePackets { get; set; }

        public bool RecycleDataBlocks { get; set; }

        /// <summary>
        /// The number of packets pooled
        /// </summary>
        public int PacketsPooled
        {
            get 
            {
                lock (pool)
                    return pool.Count;
            }
        }

        /// <summary>
        /// The number of blocks pooled.
        /// </summary>
        public int BlocksPooled
        {
            get 
            {
                lock (DataBlocks) 
                    return DataBlocks.Count;
            }
        }

        /// <summary>
        /// Number of packets requested.
        /// </summary>
        public long PacketsRequested { get; private set; }

        /// <summary>
        /// Number of packets reused.
        /// </summary>
        public long PacketsReused { get; private set; }

        /// <summary>
        /// Number of packet blocks requested.
        /// </summary>
        public long BlocksRequested { get; private set; }

        /// <summary>
        /// Number of packet blocks reused.
        /// </summary>
        public long BlocksReused { get; private set; }

        private PacketPool()
        {
            // defaults
            RecyclePackets = true;
            RecycleDataBlocks = true;
        }

        /// <summary>
        /// Gets a packet of the given type.
        /// </summary>
        /// <param name='type'></param>
        /// <returns>Guaranteed to always return a packet, whether from the pool or newly constructed.</returns>
        public Packet GetPacket(PacketType type)
        {
            PacketsRequested++;

            Packet packet;

            if (!RecyclePackets)
                return Packet.BuildPacket(type);

            lock (pool)
            {
                if (!pool.ContainsKey(type) || pool[type] == null || (pool[type]).Count == 0)
                {
//                    m_log.DebugFormat("[PACKETPOOL]: Building {0} packet", type);

                    // Creating a new packet if we cannot reuse an old package
                    packet = Packet.BuildPacket(type);
                }
                else
                {
//                    m_log.DebugFormat("[PACKETPOOL]: Pulling {0} packet", type);

                    // Recycle old packages
                    PacketsReused++;

                    packet = pool[type].Pop();
                }
            }

            return packet;
        }

        private static PacketType GetType(byte[] bytes)
        {
            ushort id;
            PacketFrequency freq;
            bool isZeroCoded = (bytes[0] & Helpers.MSG_ZEROCODED) != 0;

            if (bytes[6] == 0xFF)
            {
                if (bytes[7] == 0xFF)
                {
                    freq = PacketFrequency.Low;
                    if (isZeroCoded && bytes[8] == 0)
                        id = bytes[10];
                    else
                        id = (ushort)((bytes[8] << 8) + bytes[9]);
                }
                else
                {
                    freq = PacketFrequency.Medium;
                    id = bytes[7];
                }
            }
            else
            {
                freq = PacketFrequency.High;
                id = bytes[6];
            }

            return Packet.GetType(id, freq);
        }

        public Packet GetPacket(byte[] bytes, ref int packetEnd, byte[] zeroBuffer)
        {
            PacketType type = GetType(bytes);

//            Array.Clear(zeroBuffer, 0, zeroBuffer.Length);

            int i = 0;
            Packet packet = GetPacket(type);
            if (packet == null)
                m_log.WarnFormat("[PACKETPOOL]: Failed to get packet of type {0}", type);
            else
                packet.FromBytes(bytes, ref i, ref packetEnd, zeroBuffer);

            return packet;
        }

        /// <summary>
        /// Return a packet to the packet pool
        /// </summary>
        /// <param name="packet"></param>
        public void ReturnPacket(Packet packet)
        {
            if (RecycleDataBlocks)
            {
                switch (packet.Type)
                {
                    case PacketType.ObjectUpdate:
                        ObjectUpdatePacket oup = (ObjectUpdatePacket)packet;

                        foreach (ObjectUpdatePacket.ObjectDataBlock oupod in oup.ObjectData)
                            ReturnDataBlock<ObjectUpdatePacket.ObjectDataBlock>(oupod);

                        oup.ObjectData = null;
                        break;

                    case PacketType.ImprovedTerseObjectUpdate:
                        ImprovedTerseObjectUpdatePacket itoup = (ImprovedTerseObjectUpdatePacket)packet;

                        foreach (ImprovedTerseObjectUpdatePacket.ObjectDataBlock itoupod in itoup.ObjectData)
                            ReturnDataBlock<ImprovedTerseObjectUpdatePacket.ObjectDataBlock>(itoupod);

                        itoup.ObjectData = null;
                        break;
                }
            }

            if (RecyclePackets)
            {
                switch (packet.Type)
                {
                    // List pooling packets here
                    case PacketType.AgentUpdate:
                    case PacketType.PacketAck:
                    case PacketType.ObjectUpdate:
                    case PacketType.ImprovedTerseObjectUpdate:
                        lock (pool)
                        {
                            PacketType type = packet.Type;

                            if (!pool.ContainsKey(type))
                            {
                                pool[type] = new Stack<Packet>();
                            }

                            if ((pool[type]).Count < 50)
                            {
//                                m_log.DebugFormat("[PACKETPOOL]: Pushing {0} packet", type);

                                pool[type].Push(packet);
                            }
                        }
                        break;
                    
                    // Other packets wont pool
                    default:
                        return;
                }
            }
        }

        public T GetDataBlock<T>() where T: new()
        {
            lock (DataBlocks)
            {
                BlocksRequested++;

                Stack<Object> s;

                if (DataBlocks.TryGetValue(typeof(T), out s))
                {
                    if (s.Count > 0)
                    {
                        BlocksReused++;
                        return (T)s.Pop();
                    }
                }
                else
                {
                    DataBlocks[typeof(T)] = new Stack<Object>();
                }
                
                return new T();
            }
        }

        public void ReturnDataBlock<T>(T block) where T: new()
        {
            if (block == null)
                return;

            lock (DataBlocks)
            {
                if (!DataBlocks.ContainsKey(typeof(T)))
                    DataBlocks[typeof(T)] = new Stack<Object>();

                if (DataBlocks[typeof(T)].Count < 50)
                    DataBlocks[typeof(T)].Push(block);
            }
        }
    }
}