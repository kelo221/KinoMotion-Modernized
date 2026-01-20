//
// Kino/Motion - Motion blur effect
//
// Copyright (C) 2016 Keijiro Takahashi
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace Kino
{
    public partial class Motion
    {
        // Reconstruction filter for shutter speed simulation
        public class ReconstructionFilter
        {
            #region Predefined constants

            // Maximum blur radius (in pixels)
            const int kMaxBlurRadius = 10;

            #endregion

            #region Private fields

            // Modern GraphicsFormat API (Unity 6+) for better bandwidth efficiency
            GraphicsFormat _vectorFormat;
            GraphicsFormat _packedFormat;
            
            // Legacy fallback
            RenderTextureFormat _vectorRTFormat;
            RenderTextureFormat _packedRTFormat;
            
            bool _useGraphicsFormat;

            Material _material;
            
            bool _debugLogging;

            #endregion

            #region Public methods

            public ReconstructionFilter(bool debugLogging = false)
            {
                _debugLogging = debugLogging;
                var shader = Shader.Find("Hidden/Kino/Motion/Reconstruction");
                
                if (shader == null)
                {
                    UnityEngine.Debug.LogError("[KinoMotion] Reconstruction shader not found!");
                }
                else if (!shader.isSupported)
                {
                    UnityEngine.Debug.LogError("[KinoMotion] Reconstruction shader is not supported!");
                }
                else if (!InitializeFormats())
                {
                    UnityEngine.Debug.LogError("[KinoMotion] Required texture formats not supported!");
                }
                else
                {
                    _material = new Material(shader);
                    _material.hideFlags = HideFlags.DontSave;
                    if (_debugLogging) UnityEngine.Debug.Log($"[KinoMotion] Reconstruction filter initialized - Using {(_useGraphicsFormat ? "GraphicsFormat" : "RenderTextureFormat")}");
                }
            }

            public void Release()
            {
                if (_material != null) DestroyImmediate(_material);
                _material = null;
            }

            /// <summary>
            /// Set camera filtering parameters. Call before ProcessImage.
            /// </summary>
            /// <param name="filterCameraMotion">If true, subtract camera motion from velocity</param>
            /// <param name="invVP">Current frame's inverse view-projection matrix</param>
            /// <param name="prevVP">Previous frame's view-projection matrix</param>
            /// <param name="invProj">Current frame's inverse projection matrix</param>
            public void SetCameraFilter(bool filterCameraMotion, Matrix4x4 invVP, Matrix4x4 prevVP, Matrix4x4 invProj)
            {
                if (_material == null) return;
                
                _material.SetFloat("_FilterCameraMotion", filterCameraMotion ? 1.0f : 0.0f);
                _material.SetMatrix("_InvVP", invVP);
                _material.SetMatrix("_PrevVP", prevVP);
                _material.SetMatrix("_CameraInvProj", invProj);
            }

            public void ProcessImage(
                float shutterAngle, int sampleCount,
                RenderTexture source, RenderTexture destination
            )
            {
                // If the shader isn't supported, simply blit and return.
                if (_material == null) {
                    Graphics.Blit(source, destination);
                    return;
                }

                // Calculate the maximum blur radius in pixels.
                var maxBlurPixels = (int)(kMaxBlurRadius * source.height / 100);

                // Calculate the TileMax size.
                // It should be a multiple of 8 and larger than maxBlur.
                var tileSize = ((maxBlurPixels - 1) / 8 + 1) * 8;

                // 1st pass - Velocity/depth packing
                var velocityScale = shutterAngle / 360;
                _material.SetFloat("_VelocityScale", velocityScale);
                _material.SetFloat("_MaxBlurRadius", maxBlurPixels);
                _material.SetFloat("_RcpMaxBlurRadius", 1.0f / maxBlurPixels);

                var vbuffer = GetTemporaryRT(source, 1, _packedRTFormat);
                Graphics.Blit(null, vbuffer, _material, 0);

                // 2nd pass - 1/2 TileMax filter
                var tile2 = GetTemporaryRT(source, 2, _vectorRTFormat);
                Graphics.Blit(vbuffer, tile2, _material, 1);

                // 3rd pass - 1/2 TileMax filter
                var tile4 = GetTemporaryRT(source, 4, _vectorRTFormat);
                Graphics.Blit(tile2, tile4, _material, 2);
                ReleaseTemporaryRT(tile2);

                // 4th pass - 1/2 TileMax filter
                var tile8 = GetTemporaryRT(source, 8, _vectorRTFormat);
                Graphics.Blit(tile4, tile8, _material, 2);
                ReleaseTemporaryRT(tile4);

                // 5th pass - Last TileMax filter (reduce to tileSize)
                var tileMaxOffs = Vector2.one * (tileSize / 8.0f - 1) * -0.5f;
                _material.SetVector("_TileMaxOffs", tileMaxOffs);
                _material.SetInt("_TileMaxLoop", tileSize / 8);

                var tile = GetTemporaryRT(source, tileSize, _vectorRTFormat);
                Graphics.Blit(tile8, tile, _material, 3);
                ReleaseTemporaryRT(tile8);

                // 6th pass - NeighborMax filter
                var neighborMax = GetTemporaryRT(source, tileSize, _vectorRTFormat);
                Graphics.Blit(tile, neighborMax, _material, 4);
                ReleaseTemporaryRT(tile);

                // 7th pass - Reconstruction pass
                _material.SetFloat("_LoopCount", Mathf.Clamp(sampleCount, 2, 64) / 2);
                _material.SetTexture("_NeighborMaxTex", neighborMax);
                _material.SetTexture("_VelocityTex", vbuffer);
                Graphics.Blit(source, destination, _material, 5);

                // Cleaning up
                ReleaseTemporaryRT(vbuffer);
                ReleaseTemporaryRT(neighborMax);
            }

            // Prepare textures for async compute reconstruction
            // Returns the IDs of velocity buffer and neighborMax for compute shader to use
            public void PrepareForReconstruction(
                CommandBuffer cmd, float shutterAngle, int sampleCount,
                RenderTargetIdentifier velocityDest, RenderTargetIdentifier neighborMaxDest,
                int tileSize
            )
            {
                var maxBlurPixels = (int)(kMaxBlurRadius * Screen.height / 1080.0f);

                var velocityScale = shutterAngle / 360;
                _material.SetFloat("_VelocityScale", velocityScale);
                _material.SetFloat("_MaxBlurRadius", maxBlurPixels);
                _material.SetFloat("_RcpMaxBlurRadius", 1.0f / maxBlurPixels);

                // Initialize temporary tiles (internal only)
                int tile2, tile4, tile8, tile;

                if (_useGraphicsFormat)
                {
                    // 1. Velocity Buffer (Write to external dest)
                    cmd.Blit(null, velocityDest, _material, 0);

                    // 2. Tile Generation (Internal temps)
                    tile2 = GetTemporaryRT(cmd, "_Tile2", 2, _vectorFormat);
                    cmd.Blit(velocityDest, tile2, _material, 1);

                    tile4 = GetTemporaryRT(cmd, "_Tile4", 4, _vectorFormat);
                    cmd.Blit(tile2, tile4, _material, 2);
                    ReleaseTemporaryRT(cmd, tile2);

                    tile8 = GetTemporaryRT(cmd, "_Tile8", 8, _vectorFormat);
                    cmd.Blit(tile4, tile8, _material, 2);
                    ReleaseTemporaryRT(cmd, tile4);

                    _material.SetVector("_TileMaxOffs", Vector2.one * (tileSize / 8.0f - 1) * -0.5f);
                    _material.SetInt("_TileMaxLoop", tileSize / 8);

                    tile = GetTemporaryRT(cmd, "_Tile", tileSize, _vectorFormat);
                    cmd.Blit(tile8, tile, _material, 3);
                    ReleaseTemporaryRT(cmd, tile8);

                    // 3. NeighborMax (Write to external dest)
                    cmd.Blit(tile, neighborMaxDest, _material, 4);
                    ReleaseTemporaryRT(cmd, tile);
                }
                else
                {
                    // Legacy Fallback
                    cmd.Blit(null, velocityDest, _material, 0);

                    tile2 = GetTemporaryRT(cmd, "_Tile2", 2, _vectorRTFormat);
                    cmd.Blit(velocityDest, tile2, _material, 1);

                    tile4 = GetTemporaryRT(cmd, "_Tile4", 4, _vectorRTFormat);
                    cmd.Blit(tile2, tile4, _material, 2);
                    ReleaseTemporaryRT(cmd, tile2);

                    tile8 = GetTemporaryRT(cmd, "_Tile8", 8, _vectorRTFormat);
                    cmd.Blit(tile4, tile8, _material, 2);
                    ReleaseTemporaryRT(cmd, tile4);

                    _material.SetVector("_TileMaxOffs", Vector2.one * (tileSize / 8.0f - 1) * -0.5f);
                    _material.SetInt("_TileMaxLoop", tileSize / 8);

                    tile = GetTemporaryRT(cmd, "_Tile", tileSize, _vectorRTFormat);
                    cmd.Blit(tile8, tile, _material, 3);
                    ReleaseTemporaryRT(cmd, tile8);

                    cmd.Blit(tile, neighborMaxDest, _material, 4);
                    ReleaseTemporaryRT(cmd, tile);
                }

                // Set global textures for compute shader to access (Optional, but good for Fallback)
                cmd.SetGlobalTexture("_VelocityTex", velocityDest);
                cmd.SetGlobalTexture("_NeighborMaxTex", neighborMaxDest);
                cmd.SetGlobalFloat("_LoopCount", Mathf.Clamp(sampleCount, 2, 64) / 2);
                cmd.SetGlobalFloat("_MaxBlurRadius", maxBlurPixels);
                cmd.SetGlobalFloat("_RcpMaxBlurRadius", 1.0f / maxBlurPixels);
            }

            public void ProcessImage(
                CommandBuffer cmd, float shutterAngle, int sampleCount,
                RenderTargetIdentifier source, RenderTargetIdentifier destination
            )
            {
                // If the shader isn't supported, simply blit and return.
                if (_material == null) {
                    cmd.Blit(source, destination);
                    return;
                }

                // Calculate the maximum blur radius in pixels.
                var maxBlurPixels = (int)(kMaxBlurRadius * Screen.height / 100);

                // Calculate the TileMax size.
                // It should be a multiple of 8 and larger than maxBlur.
                var tileSize = ((maxBlurPixels - 1) / 8 + 1) * 8;

                // 1st pass - Velocity/depth packing
                var velocityScale = shutterAngle / 360;
                _material.SetFloat("_VelocityScale", velocityScale);
                _material.SetFloat("_MaxBlurRadius", maxBlurPixels);
                _material.SetFloat("_RcpMaxBlurRadius", 1.0f / maxBlurPixels);

                // Use GraphicsFormat if available for better bandwidth
                int vbuffer, tile2, tile4, tile8, tile, neighborMax;
                
                if (_useGraphicsFormat)
                {
                    vbuffer = GetTemporaryRT(cmd, "_VelocityBuffer", 1, _packedFormat);
                    cmd.Blit(null, vbuffer, _material, 0);

                    tile2 = GetTemporaryRT(cmd, "_Tile2", 2, _vectorFormat);
                    cmd.Blit(vbuffer, tile2, _material, 1);

                    tile4 = GetTemporaryRT(cmd, "_Tile4", 4, _vectorFormat);
                    cmd.Blit(tile2, tile4, _material, 2);
                    ReleaseTemporaryRT(cmd, tile2);

                    tile8 = GetTemporaryRT(cmd, "_Tile8", 8, _vectorFormat);
                    cmd.Blit(tile4, tile8, _material, 2);
                    ReleaseTemporaryRT(cmd, tile4);

                    _material.SetVector("_TileMaxOffs", Vector2.one * (tileSize / 8.0f - 1) * -0.5f);
                    _material.SetInt("_TileMaxLoop", tileSize / 8);

                    tile = GetTemporaryRT(cmd, "_Tile", tileSize, _vectorFormat);
                    cmd.Blit(tile8, tile, _material, 3);
                    ReleaseTemporaryRT(cmd, tile8);

                    neighborMax = GetTemporaryRT(cmd, "_NeighborMax", tileSize, _vectorFormat);
                    cmd.Blit(tile, neighborMax, _material, 4);
                    ReleaseTemporaryRT(cmd, tile);
                }
                else
                {
                    // Fallback to RenderTextureFormat
                    vbuffer = GetTemporaryRT(cmd, "_VelocityBuffer", 1, _packedRTFormat);
                    cmd.Blit(null, vbuffer, _material, 0);

                    tile2 = GetTemporaryRT(cmd, "_Tile2", 2, _vectorRTFormat);
                    cmd.Blit(vbuffer, tile2, _material, 1);

                    tile4 = GetTemporaryRT(cmd, "_Tile4", 4, _vectorRTFormat);
                    cmd.Blit(tile2, tile4, _material, 2);
                    ReleaseTemporaryRT(cmd, tile2);

                    tile8 = GetTemporaryRT(cmd, "_Tile8", 8, _vectorRTFormat);
                    cmd.Blit(tile4, tile8, _material, 2);
                    ReleaseTemporaryRT(cmd, tile4);

                    _material.SetVector("_TileMaxOffs", Vector2.one * (tileSize / 8.0f - 1) * -0.5f);
                    _material.SetInt("_TileMaxLoop", tileSize / 8);

                    tile = GetTemporaryRT(cmd, "_Tile", tileSize, _vectorRTFormat);
                    cmd.Blit(tile8, tile, _material, 3);
                    ReleaseTemporaryRT(cmd, tile8);

                    neighborMax = GetTemporaryRT(cmd, "_NeighborMax", tileSize, _vectorRTFormat);
                    cmd.Blit(tile, neighborMax, _material, 4);
                    ReleaseTemporaryRT(cmd, tile);
                }

                // 7th pass - Reconstruction pass
                _material.SetFloat("_LoopCount", Mathf.Clamp(sampleCount, 2, 64) / 2);
                cmd.SetGlobalTexture("_NeighborMaxTex", neighborMax);
                cmd.SetGlobalTexture("_VelocityTex", vbuffer);
                cmd.Blit(source, destination, _material, 5);

                // Cleaning up
                ReleaseTemporaryRT(cmd, vbuffer);
                ReleaseTemporaryRT(cmd, neighborMax);
            }

            #endregion

            #region Private methods

            bool InitializeFormats()
            {
                // Try modern GraphicsFormat first (Unity 6+)
                _vectorFormat = GraphicsFormat.R16G16_SFloat;
                _packedFormat = GraphicsFormat.A2B10G10R10_UNormPack32;
                
                if (SystemInfo.IsFormatSupported(_vectorFormat, GraphicsFormatUsage.Render) &&
                    SystemInfo.IsFormatSupported(_packedFormat, GraphicsFormatUsage.Render))
                {
                    _useGraphicsFormat = true;
                    return true;
                }
                
                // Fallback to legacy RenderTextureFormat
                _vectorRTFormat = RenderTextureFormat.RGHalf;
                _packedRTFormat = RenderTextureFormat.ARGB2101010;
                
                if (SystemInfo.SupportsRenderTextureFormat(_vectorRTFormat) &&
                    SystemInfo.SupportsRenderTextureFormat(_packedRTFormat))
                {
                    _useGraphicsFormat = false;
                    return true;
                }
                
                return false;
            }

            RenderTexture GetTemporaryRT(
                Texture source, int divider, RenderTextureFormat format
            )
            {
                var w = source.width / divider;
                var h = source.height / divider;
                var linear = RenderTextureReadWrite.Linear;
                var rt = RenderTexture.GetTemporary(w, h, 0, format, linear);
                rt.filterMode = FilterMode.Point;
                return rt;
            }

            void ReleaseTemporaryRT(RenderTexture rt)
            {
                RenderTexture.ReleaseTemporary(rt);
            }

            // CommandBuffer versions with GraphicsFormat support
            int GetTemporaryRT(
                CommandBuffer cmd, string name, int divider, GraphicsFormat format
            )
            {
                var w = Screen.width / divider;
                var h = Screen.height / divider;
                int id = Shader.PropertyToID(name);
                
                var desc = new RenderTextureDescriptor(w, h);
                desc.graphicsFormat = format;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                
                cmd.GetTemporaryRT(id, desc, FilterMode.Point);
                return id;
            }
            
            int GetTemporaryRT(
                CommandBuffer cmd, string name, int divider, RenderTextureFormat format
            )
            {
                var w = Screen.width / divider;
                var h = Screen.height / divider;
                int id = Shader.PropertyToID(name);
                cmd.GetTemporaryRT(id, w, h, 0, FilterMode.Point, format, RenderTextureReadWrite.Linear);
                return id;
            }

            void ReleaseTemporaryRT(CommandBuffer cmd, int id)
            {
                cmd.ReleaseTemporaryRT(id);
            }

            #endregion
        }
    }
}
