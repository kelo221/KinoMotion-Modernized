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

namespace Kino
{
    public partial class Motion
    {
        //
        // Multiple frame blending filter
        //
        // This filter acts like a finite impulse response filter; stores
        // succeeding four frames and calculate the weighted average of them.
        //
        // To save memory, it compresses frame images with the 4:2:2 chroma
        // subsampling scheme. This requires MRT support. If the current
        // environment doesn't support MRT, it tries to use one of the 16-bit
        // texture format instead. Unfortunately, some GPUs don't support
        // 16-bit color render targets. So, in the worst case, it ends up with
        // using 32-bit raw textures.
        //
        public class FrameBlendingFilter
        {
            #region Public methods

            bool _debugLogging;
            
            public FrameBlendingFilter(bool debugLogging = false)
            {
                _debugLogging = debugLogging;
                _useCompression = CheckSupportCompression();
                _rawTextureFormat = GetPreferredRenderTextureFormat();

                var shader = Shader.Find("Hidden/Kino/Motion/FrameBlending");
                
                if (shader == null)
                {
                    UnityEngine.Debug.LogError("[KinoMotion] FrameBlending shader not found!");
                    return;
                }
                
                _material = new Material(shader);
                _material.hideFlags = HideFlags.DontSave;
                
                if (_debugLogging) UnityEngine.Debug.Log("[KinoMotion] FrameBlending filter initialized successfully");

                _frameList = new Frame[4];
            }

            public void Release()
            {
                if (_material != null) DestroyImmediate(_material);
                _material = null;

                foreach (var frame in _frameList) frame.Release();
            }

            public void PushFrame(RenderTexture source)
            {
                // Push the frame list.
                var temp = _frameList[3];
                _frameList[3] = _frameList[2];
                _frameList[2] = _frameList[1];
                _frameList[1] = _frameList[0];
                _frameList[0] = temp;

                // Record the current frame.
                if (_useCompression)
                    _frameList[0].MakeRecord(source, _material);
                else
                    _frameList[0].MakeRecordRaw(source, _rawTextureFormat);
            }

            public void BlendFrames(float strength, RenderTexture source, RenderTexture destination)
            {
                var t = Time.time;

                var f1 = GetFrameRelative(-1);
                var f2 = GetFrameRelative(-2);
                var f3 = GetFrameRelative(-3);
                var f4 = GetFrameRelative(-4);

                _material.SetTexture("_History1LumaTex", f1.lumaTexture);
                _material.SetTexture("_History2LumaTex", f2.lumaTexture);
                _material.SetTexture("_History3LumaTex", f3.lumaTexture);
                _material.SetTexture("_History4LumaTex", f4.lumaTexture);

                _material.SetTexture("_History1ChromaTex", f1.chromaTexture);
                _material.SetTexture("_History2ChromaTex", f2.chromaTexture);
                _material.SetTexture("_History3ChromaTex", f3.chromaTexture);
                _material.SetTexture("_History4ChromaTex", f4.chromaTexture);

                _material.SetFloat("_History1Weight", f1.CalculateWeight(strength, t));
                _material.SetFloat("_History2Weight", f2.CalculateWeight(strength, t));
                _material.SetFloat("_History3Weight", f3.CalculateWeight(strength, t));
                _material.SetFloat("_History4Weight", f4.CalculateWeight(strength, t));

                Graphics.Blit(source, destination, _material, _useCompression ? 1 : 2);
            }

            // CommandBuffer overloads for hybrid architecture
            public void PushFrame(CommandBuffer cmd, RenderTargetIdentifier source)
            {
                // Push the frame list.
                var temp = _frameList[3];
                _frameList[3] = _frameList[2];
                _frameList[2] = _frameList[1];
                _frameList[1] = _frameList[0];
                _frameList[0] = temp;

                // Record the current frame.
                if (_useCompression)
                    _frameList[0].MakeRecord(cmd, source, _material);
                else
                    _frameList[0].MakeRecordRaw(cmd, source, _rawTextureFormat);
            }

            public void BlendFrames(CommandBuffer cmd, float strength, RenderTargetIdentifier source, RenderTargetIdentifier destination)
            {
                var t = Time.time;

                var f1 = GetFrameRelative(-1);
                var f2 = GetFrameRelative(-2);
                var f3 = GetFrameRelative(-3);
                var f4 = GetFrameRelative(-4);

                // Use RenderTexture references directly
                cmd.SetGlobalTexture("_History1LumaTex", f1.lumaTexture);
                cmd.SetGlobalTexture("_History2LumaTex", f2.lumaTexture);
                cmd.SetGlobalTexture("_History3LumaTex", f3.lumaTexture);
                cmd.SetGlobalTexture("_History4LumaTex", f4.lumaTexture);

                cmd.SetGlobalTexture("_History1ChromaTex", f1.chromaTexture);
                cmd.SetGlobalTexture("_History2ChromaTex", f2.chromaTexture);
                cmd.SetGlobalTexture("_History3ChromaTex", f3.chromaTexture);
                cmd.SetGlobalTexture("_History4ChromaTex", f4.chromaTexture);

                _material.SetFloat("_History1Weight", f1.CalculateWeight(strength, t));
                _material.SetFloat("_History2Weight", f2.CalculateWeight(strength, t));
                _material.SetFloat("_History3Weight", f3.CalculateWeight(strength, t));
                _material.SetFloat("_History4Weight", f4.CalculateWeight(strength, t));

                cmd.Blit(source, destination, _material, _useCompression ? 1 : 2);
            }

            #endregion

            #region Private members

            bool _useCompression;
            RenderTextureFormat _rawTextureFormat;

            Material _material;
            Frame[] _frameList;

            Frame GetFrameRelative(int offset)
            {
                var index = (Time.frameCount - offset) % _frameList.Length;
                return _frameList[index];
            }

            #endregion

            #region Frame struct

            struct Frame
            {
                public RenderTexture lumaTexture;
                public RenderTexture chromaTexture;
                
                // Texture IDs for CommandBuffer workflow
                public int lumaTextureID;
                public int chromaTextureID;
                
                public float time;

                public float CalculateWeight(float strength, float currentTime)
                {
                    if (time == 0) return 0;
                    var coeff = Mathf.Lerp(80.0f, 16.0f, strength);
                    return Mathf.Exp((time - currentTime) * coeff);
                }

                public void Release()
                {
                    if (lumaTexture != null) RenderTexture.ReleaseTemporary(lumaTexture);
                    if (chromaTexture != null) RenderTexture.ReleaseTemporary(chromaTexture);

                    lumaTexture = null;
                    chromaTexture = null;
                    lumaTextureID = 0;
                    chromaTextureID = 0;
                }

                public void MakeRecord(RenderTexture source, Material material)
                {
                    Release();

                    lumaTexture = RenderTexture.GetTemporary(
                        source.width, source.height, 0,
                        RenderTextureFormat.R8, RenderTextureReadWrite.Linear
                    );
                    lumaTexture.filterMode = FilterMode.Point;

                    chromaTexture = RenderTexture.GetTemporary(
                        source.width, source.height, 0,
                        RenderTextureFormat.R8, RenderTextureReadWrite.Linear
                    );
                    chromaTexture.filterMode = FilterMode.Point;

                    var mrt = new RenderBuffer[] { lumaTexture.colorBuffer, chromaTexture.colorBuffer };
                    Graphics.SetRenderTarget(mrt, lumaTexture.depthBuffer);
                    Graphics.Blit(source, material, 0);

                    time = Time.time;
                }

                public void MakeRecordRaw(RenderTexture source, RenderTextureFormat format)
                {
                    Release();

                    lumaTexture = RenderTexture.GetTemporary(
                        source.width, source.height, 0, format
                    );
                    lumaTexture.filterMode = FilterMode.Point;

                    Graphics.Blit(source, lumaTexture);

                    time = Time.time;
                }

                // CommandBuffer versions - use persistent RenderTextures for history
                public void MakeRecord(CommandBuffer cmd, RenderTargetIdentifier source, Material material)
                {
                    Release();

                    // Use persistent RenderTextures for frame history (not CommandBuffer temps)
                    lumaTexture = RenderTexture.GetTemporary(
                        Screen.width, Screen.height, 0,
                        RenderTextureFormat.R8, RenderTextureReadWrite.Linear
                    );
                    lumaTexture.filterMode = FilterMode.Point;

                    chromaTexture = RenderTexture.GetTemporary(
                        Screen.width, Screen.height, 0,
                        RenderTextureFormat.R8, RenderTextureReadWrite.Linear
                    );
                    chromaTexture.filterMode = FilterMode.Point;

                    // Store texture IDs for CommandBuffer reference
                    lumaTextureID = lumaTexture.GetInstanceID();
                    chromaTextureID = chromaTexture.GetInstanceID();

                    var mrt = new RenderTargetIdentifier[] { lumaTexture, chromaTexture };
                    cmd.SetRenderTarget(mrt, lumaTexture.depthBuffer);
                    cmd.Blit(source, BuiltinRenderTextureType.CurrentActive, material, 0);

                    time = Time.time;
                }

                public void MakeRecordRaw(CommandBuffer cmd, RenderTargetIdentifier source, RenderTextureFormat format)
                {
                    Release();

                    // Use persistent RenderTexture for frame history (not CommandBuffer temp)
                    lumaTexture = RenderTexture.GetTemporary(
                        Screen.width, Screen.height, 0, format
                    );
                    lumaTexture.filterMode = FilterMode.Point;

                    // Store texture ID for CommandBuffer reference
                    lumaTextureID = lumaTexture.GetInstanceID();

                    cmd.Blit(source, lumaTexture);

                    time = Time.time;
                }
            }

            #endregion

            #region Private methods

            static bool CheckSupportCompression()
            {
                return
                    SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8) &&
                    SystemInfo.supportedRenderTargetCount > 1;
            }

            static RenderTextureFormat GetPreferredRenderTextureFormat()
            {
                var candidates = new RenderTextureFormat[] {
                    RenderTextureFormat.RGB565,
                    RenderTextureFormat.ARGB1555,
                    RenderTextureFormat.Default
                };

                foreach (var f in candidates)
                    if (SystemInfo.SupportsRenderTextureFormat(f)) return f;

                return RenderTextureFormat.Default;
            }

            #endregion
        }
    }
}
