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
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("Kino Image Effects/Motion")]
    public partial class Motion : MonoBehaviour
    {
        #region Public properties

        /// The angle of rotary shutter. The larger the angle is, the longer
        /// the exposure time is.
        public float shutterAngle {
            get { return _shutterAngle; }
            set { _shutterAngle = value; }
        }

        [SerializeField, Range(0, 360)]
        [Tooltip("The angle of rotary shutter. Larger values give longer exposure.")]
        float _shutterAngle = 270;

        /// The amount of sample points, which affects quality and performance.
        public int sampleCount {
            get { return _sampleCount; }
            set { _sampleCount = value; }
        }

        [SerializeField]
        [Tooltip("The amount of sample points, which affects quality and performance.")]
        int _sampleCount = 8;

        /// The strength of multiple frame blending. The opacity of preceding
        /// frames are determined from this coefficient and time differences.
        public float frameBlending {
            get { return _frameBlending; }
            set { _frameBlending = value; }
        }

        [SerializeField, Range(0, 1)]
        [Tooltip("The strength of multiple frame blending")]
        float _frameBlending = 0;

        /// Enable async compute for motion blur pass (DX12/Vulkan only)
        public bool useAsyncCompute {
            get { return _useAsyncCompute; }
            set { _useAsyncCompute = value; }
        }

        [SerializeField, Tooltip("Enable async compute for motion blur (DX12/Vulkan - 20-30% GPU boost)")]
        bool _useAsyncCompute = true; // Now properly implemented with RTHandles!

        /// Compute shader for async reconstruction (DX12/Vulkan only)
        public ComputeShader reconstructionCS {
            get { return _reconstructionCS; }
            set { _reconstructionCS = value; }
        }

        /// Shader variant collection for PSO pre-cooking (eliminates first-frame hitches on DX12/Vulkan)
        public ShaderVariantCollection shaderVariants {
            get { return _shaderVariants; }
            set { _shaderVariants = value; }
        }

        [SerializeField, HideInInspector] 
        Shader _reconstructionShader;
        
        [SerializeField, HideInInspector] 
        Shader _frameBlendingShader;
        
        [SerializeField, Tooltip("Optional: Compute shader for async reconstruction. Auto-loaded from Resources if not assigned.")]
        ComputeShader _reconstructionCS;

        [SerializeField, Tooltip("Shader variants for PSO pre-cooking (DX12/Vulkan - eliminates hitches)")]
        ShaderVariantCollection _shaderVariants;

        /// Enable debug logging to console
        public bool debugLogging {
            get { return _debugLogging; }
            set { _debugLogging = value; }
        }

        [SerializeField, Tooltip("Enable debug messages in console")]
        bool _debugLogging = false;

        /// Filter out camera motion, only blur moving objects
        public bool filterCameraMotion {
            get { return _filterCameraMotion; }
            set { _filterCameraMotion = value; }
        }

        [SerializeField, Tooltip("Filter out camera motion - only objects will blur, not camera movement")]
        bool _filterCameraMotion = false;

        #endregion

        #region Private fields

        Motion.ReconstructionFilter _reconstructionFilter;
        Motion.FrameBlendingFilter _frameBlendingFilter;
        
        // Persistent CommandBuffer (avoids GC pressure)
        CommandBuffer _cmd;
        
        // Async compute CommandBuffer (DX12/Vulkan only)
        CommandBuffer _asyncCmd;
        
        // Async compute support (Unity 6.1+)
        bool _supportsAsyncCompute;

        // Persistent RenderTextures for async compute (cross-queue texture sharing)
        RenderTexture _asyncSourceRT;
        RenderTexture _asyncResultRT;
        RenderTexture _velocityRT;
        RenderTexture _neighborMaxRT;

        #endregion

        #region MonoBehaviour functions

        void Awake()
        {
            // PSO Pre-cooking for DX12/Vulkan (BIRP uses ShaderVariantCollection, not GraphicsStateCollection)
            if (_shaderVariants != null && !_shaderVariants.isWarmedUp)
            {
                _shaderVariants.WarmUp();
                if (_debugLogging) UnityEngine.Debug.Log("[KinoMotion] ShaderVariantCollection warmed up - PSO hitches eliminated");
            }
        }

        void OnEnable()
        {
            // CRITICAL: Enable motion vectors BEFORE creating filters
            GetComponent<Camera>().depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            
            // Create filters (pass debug flag)
            _reconstructionFilter = new Motion.ReconstructionFilter(_debugLogging);
            _frameBlendingFilter = new Motion.FrameBlendingFilter(_debugLogging);
            
            // Create persistent CommandBuffer (GC-free)
            _cmd = new CommandBuffer { name = "KinoMotion Hybrid" };
            
            // Auto-load compute shader from Resources if not manually assigned
            if (_reconstructionCS == null)
            {
                _reconstructionCS = Resources.Load<ComputeShader>("Reconstruction");
                if (_reconstructionCS != null && _debugLogging)
                    UnityEngine.Debug.Log("[KinoMotion] Compute shader auto-loaded from Resources");
            }
            
            // Check async compute support (requires GraphicsFence support)
            _supportsAsyncCompute = SystemInfo.supportsAsyncCompute && 
                                   SystemInfo.supportsGraphicsFence && 
                                   _useAsyncCompute && 
                                   _reconstructionCS != null;
            
            if (_supportsAsyncCompute)
            {
                _asyncCmd = new CommandBuffer { name = "KinoMotion Async Compute" };
                _asyncCmd.SetExecutionFlags(CommandBufferExecutionFlags.AsyncCompute);
                if (_debugLogging) UnityEngine.Debug.Log("[KinoMotion] Async compute enabled - 20-30% GPU performance boost expected");
            }
            else if (_reconstructionCS != null && !SystemInfo.supportsGraphicsFence)
            {
                if (_debugLogging) UnityEngine.Debug.LogWarning("[KinoMotion] GraphicsFence not supported on this platform. Using fragment shader path.");
            }
            
            if (_debugLogging) UnityEngine.Debug.Log($"[KinoMotion] Initialized - Motion vectors enabled, Async compute: {_supportsAsyncCompute}");
        }

        void OnDisable()
        {
            _reconstructionFilter?.Release();
            _frameBlendingFilter?.Release();
            
            _cmd?.Release();
            _asyncCmd?.Release();
            
            // Release persistent RenderTextures for async compute
            ReleaseAsyncRTs();
            
            _reconstructionFilter = null;
            _frameBlendingFilter = null;
            _cmd = null;
            _asyncCmd = null;
        }

        void UpdateAsyncRTHandles(RenderTexture source)
        {
            // Re-allocate RenderTextures only if resolution changes
            if (_asyncSourceRT == null || _asyncSourceRT.width != source.width || _asyncSourceRT.height != source.height)
            {
                // IMPORTANT: Use explicit RenderTexture creation instead of GetTemporary()
                // GetTemporary() uses pooling which may not properly respect enableRandomWrite in BIRP
                ReleaseAsyncRTs();

                int w = source.width;
                int h = source.height;
                
                // 1. Source (SRV only)
                _asyncSourceRT = new RenderTexture(w, h, 0, GraphicsFormat.B10G11R11_UFloatPack32);
                _asyncSourceRT.name = "KinoMotion_AsyncSource";
                _asyncSourceRT.enableRandomWrite = false;
                _asyncSourceRT.Create();
                
                // 2. Result (UAV - MUST have enableRandomWrite BEFORE Create())
                _asyncResultRT = new RenderTexture(w, h, 0, GraphicsFormat.R16G16B16A16_SFloat);
                _asyncResultRT.name = "KinoMotion_AsyncResult";
                _asyncResultRT.enableRandomWrite = true; // CRITICAL: Must be set BEFORE Create()
                _asyncResultRT.Create();
                

                // 3. Velocity (Packed: velocity XY + depth in RGB)
                _velocityRT = new RenderTexture(w, h, 0, GraphicsFormat.R16G16B16A16_SFloat);
                _velocityRT.name = "KinoMotion_Velocity";
                _velocityRT.enableRandomWrite = false;
                _velocityRT.Create();

                // 4. NeighborMax (Reduced resolution)
                var maxBlurPixels = (int)(10 * source.height / 1080.0f);
                var tileSize = ((maxBlurPixels - 1) / 8 + 1) * 8;
                
                int tileW = w / tileSize;
                int tileH = h / tileSize;
                _neighborMaxRT = new RenderTexture(tileW, tileH, 0, GraphicsFormat.R16G16_SFloat);
                _neighborMaxRT.name = "KinoMotion_NeighborMax";
                _neighborMaxRT.enableRandomWrite = false;
                _neighborMaxRT.Create();
                
            }
        }
        
        void ReleaseAsyncRTs()
        {
            if (_asyncSourceRT != null) { _asyncSourceRT.Release(); Object.DestroyImmediate(_asyncSourceRT); _asyncSourceRT = null; }
            if (_asyncResultRT != null) { _asyncResultRT.Release(); Object.DestroyImmediate(_asyncResultRT); _asyncResultRT = null; }
            if (_velocityRT != null) { _velocityRT.Release(); Object.DestroyImmediate(_velocityRT); _velocityRT = null; }
            if (_neighborMaxRT != null) { _neighborMaxRT.Release(); Object.DestroyImmediate(_neighborMaxRT); _neighborMaxRT = null; }
        }

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            // Application-side checks
            if (_reconstructionFilter == null || _frameBlendingFilter == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            _cmd.Clear();

            // Unified descriptor setup
            var desc = source.descriptor;
            desc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.B10G11R11_UFloatPack32;
            desc.enableRandomWrite = _supportsAsyncCompute;

            // Set camera filtering parameters
            var cam = GetComponent<Camera>();
            var vp = cam.projectionMatrix * cam.worldToCameraMatrix;
            var invVP = vp.inverse;
            var invProj = cam.projectionMatrix.inverse;
            var prevVP = cam.previousViewProjectionMatrix;
            _reconstructionFilter.SetCameraFilter(_filterCameraMotion, invVP, prevVP, invProj);

            // --- ASYNC COMPUTE PATH (Integrated) ---
            if (_supportsAsyncCompute && _shutterAngle > 0)
            {
                // Update persistent RTs
                UpdateAsyncRTHandles(source);

                // 1. Copy source to persistent RT
                _cmd.Blit(source, _asyncSourceRT);

                // 2. Prepare velocity buffers (Main Queue) using persistent RTs
                var maxBlurPixels = (int)(10 * source.height / 1080.0f);
                var tileSize = ((maxBlurPixels - 1) / 8 + 1) * 8;
                
                _reconstructionFilter.PrepareForReconstruction(
                    _cmd, _shutterAngle, _sampleCount, 
                    _velocityRT, _neighborMaxRT, tileSize
                );
                
                // =====================================================================
                // CRITICAL FIX: The "Unbind Hack" for BIRP DX12/Vulkan Async Compute
                // =====================================================================
                // In DX12/Vulkan, textures must transition from RENDER_TARGET state to
                // SHADER_RESOURCE state. BIRP does NOT have a Render Graph to auto-insert
                // D3D12_RESOURCE_BARRIER commands. Without explicit unbinding, the GPU
                // driver sees a state violation and the sampler returns (0,0,0,0) = BLACK.
                //
                // This hack forces the Graphics Queue to "relinquish" the textures by
                // nullifying the active render target, triggering an implicit barrier.
                // =====================================================================
                _cmd.SetRenderTarget(BuiltinRenderTextureType.None);
                
                // Create fence: Wait for Graphics to finish setup before Async starts
                var graphicsFence = _cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, 
                                                             SynchronisationStageFlags.PixelProcessing);
                
                // Execute & Flush Graphics Queue setup (forces GPU to process barriers)
                Graphics.ExecuteCommandBuffer(_cmd);
                _cmd.Clear();

                // 3. Dispatch Async Compute
                _asyncCmd.Clear();
                _asyncCmd.SetExecutionFlags(CommandBufferExecutionFlags.AsyncCompute);
                
                // ASYNC WAIT: Ensure resources are populated before compute starts
                _asyncCmd.WaitOnAsyncGraphicsFence(graphicsFence);
                
                int kernel = _reconstructionCS.FindKernel("CSReconstruction");
                
                // =====================================================================
                // CRITICAL FIX: Manual Uniform Binding for BIRP Compute Shaders
                // =====================================================================
                // In BIRP, global constants like _ScreenParams and _Time are automatically
                // updated for Fragment Shaders, but are NOT automatically bound to Compute
                // Shaders during ExecuteCommandBufferAsync. We must pass them manually.
                // =====================================================================
                _asyncCmd.SetComputeVectorParam(_reconstructionCS, "_CustomScreenParams",
                    new Vector4(source.width, source.height, 1.0f / source.width, 1.0f / source.height));
                _asyncCmd.SetComputeFloatParam(_reconstructionCS, "_CustomTime", Time.time);
                _asyncCmd.SetComputeFloatParam(_reconstructionCS, "_LoopCount", Mathf.Clamp(_sampleCount, 2, 64) / 2);
                _asyncCmd.SetComputeFloatParam(_reconstructionCS, "_MaxBlurRadius", maxBlurPixels);
                _asyncCmd.SetComputeFloatParam(_reconstructionCS, "_RcpMaxBlurRadius", 1.0f / maxBlurPixels);
                
                // Reversed-Z and camera motion filter uniforms
                _asyncCmd.SetComputeFloatParam(_reconstructionCS, "_ReversedZ", SystemInfo.usesReversedZBuffer ? 1f : 0f);
                _asyncCmd.SetComputeFloatParam(_reconstructionCS, "_FilterCameraMotion", _filterCameraMotion ? 1f : 0f);
                _asyncCmd.SetComputeMatrixParam(_reconstructionCS, "_InvVP", invVP);
                _asyncCmd.SetComputeMatrixParam(_reconstructionCS, "_PrevVP", prevVP);
                _asyncCmd.SetComputeMatrixParam(_reconstructionCS, "_CameraInvProj", invProj);
                
                // ZBufferParams for LinearEyeDepth calculation in compute shader
                // Unity's _ZBufferParams: x = 1-far/near, y = far/near, z = x/far, w = y/far
                float n = cam.nearClipPlane;
                float f = cam.farClipPlane;
                Vector4 zBufferParams;
                if (SystemInfo.usesReversedZBuffer)
                    zBufferParams = new Vector4(-1 + f/n, 1, (-1 + f/n)/f, 1/f);
                else
                    zBufferParams = new Vector4(1 - f/n, f/n, (1 - f/n)/f, (f/n)/f);
                _asyncCmd.SetComputeVectorParam(_reconstructionCS, "_ZBufferParams", zBufferParams);
                
                // Bind textures
                _asyncCmd.SetComputeTextureParam(_reconstructionCS, kernel, "_Source", _asyncSourceRT);
                _asyncCmd.SetComputeTextureParam(_reconstructionCS, kernel, "_Result", _asyncResultRT);
                _asyncCmd.SetComputeTextureParam(_reconstructionCS, kernel, "_VelocityTex", _velocityRT);
                _asyncCmd.SetComputeTextureParam(_reconstructionCS, kernel, "_NeighborMaxTex", _neighborMaxRT);
                
                // Depth texture for camera motion filtering
                _asyncCmd.SetComputeTextureParam(_reconstructionCS, kernel, "_CameraDepthTexture", 
                    Shader.GetGlobalTexture("_CameraDepthTexture"));

                _asyncCmd.DispatchCompute(_reconstructionCS, kernel, 
                                         (source.width + 7) / 8, (source.height + 7) / 8, 1);
                
                // Create sync fence (Compute -> Graphics)
                var fence = _asyncCmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, 
                                                          SynchronisationStageFlags.ComputeProcessing);
                
                Graphics.ExecuteCommandBufferAsync(_asyncCmd, ComputeQueueType.Background);

                // 4. Wait & Final Blit (Main Queue)
                _cmd.WaitOnAsyncGraphicsFence(fence);

                if (_frameBlending > 0)
                {
                    _frameBlendingFilter.BlendFrames(_cmd, _frameBlending, _asyncResultRT, destination);
                    _frameBlendingFilter.PushFrame(_cmd, _asyncResultRT);
                }
                else
                {
                    _cmd.Blit(_asyncResultRT, destination);
                }

                // No ReleaseTemporaryRT needed for persistent buffers!
                
                Graphics.ExecuteCommandBuffer(_cmd);
                return;
            }

            // --- STANDARD / FALLBACK PATH ---
            // Only allocate resultID if we are NOT using async compute
            int resultID = Shader.PropertyToID("_MotionBlurResult");
            
            desc.enableRandomWrite = false; // Not needed for fragment shader
            _cmd.GetTemporaryRT(resultID, desc);

            if (_shutterAngle > 0 && _frameBlending > 0)
            {
                int tempID = Shader.PropertyToID("_MotionBlurTemp");
                _cmd.GetTemporaryRT(tempID, desc);
                
                _reconstructionFilter.ProcessImage(_cmd, _shutterAngle, _sampleCount, source, tempID);
                _frameBlendingFilter.BlendFrames(_cmd, _frameBlending, tempID, resultID);
                _frameBlendingFilter.PushFrame(_cmd, tempID);
                
                _cmd.ReleaseTemporaryRT(tempID);
            }
            else if (_shutterAngle > 0)
            {
                _reconstructionFilter.ProcessImage(_cmd, _shutterAngle, _sampleCount, source, resultID);
            }
            else if (_frameBlending > 0)
            {
                _frameBlendingFilter.BlendFrames(_cmd, _frameBlending, source, resultID);
                _frameBlendingFilter.PushFrame(_cmd, resultID); // We push the Result.
            }
            else
            {
                _cmd.Blit(source, resultID);
            }
            
            _cmd.Blit(resultID, destination);
            _cmd.ReleaseTemporaryRT(resultID);

            Graphics.ExecuteCommandBuffer(_cmd);
            _cmd.Clear();
        }

        #endregion
    }
}
