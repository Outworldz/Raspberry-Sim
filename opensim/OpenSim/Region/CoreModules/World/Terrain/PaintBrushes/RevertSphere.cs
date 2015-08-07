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

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Terrain.PaintBrushes
{
    public class RevertSphere : ITerrainPaintableEffect
    {
        private readonly ITerrainChannel m_revertmap;

        public RevertSphere(ITerrainChannel revertmap)
        {
            m_revertmap = revertmap;
        }

        #region ITerrainPaintableEffect Members

        public void PaintEffect(ITerrainChannel map, bool[,] mask, double rx, double ry, double rz, double strength, double duration)
        {
            strength = TerrainUtil.MetersToSphericalStrength(strength);
            duration = 0.03; //MCP Should be read from ini file
 
            if (duration > 1.0)
                duration = 1.0;
            if (duration < 0)
                return;

            int x;
            for (x = 0; x < map.Width; x++)
            {
                int y;
                for (y = 0; y < map.Height; y++)
                {
                    if (!mask[x,y])
                        continue;

                    // Calculate a sphere and add it to the heighmap
                    double z = strength;
                    z *= z;
                    z -= ((x - rx) * (x - rx)) + ((y - ry) * (y - ry));

                    if (z > 0.0)
                    {
                        z *= duration;
                        map[x, y] = (map[x, y] * (1.0 - z)) + (m_revertmap[x, y] * z);
                    }
                }
            }
        }

        #endregion
    }
}
