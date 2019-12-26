﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using IFSEngine.Animation;

//using OpenGL;
using OpenTK;
using OpenTK.Graphics.OpenGL;

using IFSEngine.Model;
using System.ComponentModel;
using IFSEngine.Model.GpuStructs;
using OpenTK.Graphics;
using OpenTK.Platform;
using System.Threading.Tasks;

namespace IFSEngine
{
    public class RendererGL
    {
        public event EventHandler DisplayFrameCompleted;
        //public event EventHandler RenderFrameCompleted;

        public bool UpdateDisplayOnRender { get; set; } = true;

        /// <summary>
        /// Number of dispatches since accumulation reset.
        /// This is needed for random generation and 0. dispatch reset
        /// </summary>
        private int dispatchCnt = 0;

        public float RenderScale { get; private set; } = 1.0f;
        public int RenderWidth => (int)(CurrentParams.ViewSettings.ImageResolution.Width * RenderScale);
        public int RenderHeight => (int)(CurrentParams.ViewSettings.ImageResolution.Height * RenderScale);

        public IFS CurrentParams { get; private set; }

        private int displayWidth = 1280, displayHeight = 720;

        public AnimationManager AnimationManager { get; set; }

        private bool invalidAccumulation = false;
        private bool invalidParams = false;

        private int _threadcnt = 1500;
        /// <summary>
        /// Performance setting: number of gpu threads
        /// TODO: autofind best fit based on hardware?
        /// </summary>
        public int ThreadCount { get => _threadcnt; set {
                _threadcnt = value;
                InvalidateAccumulation();
            } 
        }

        /// <summary>
        /// Performance setting: Number of iterations per dispatch.
        /// TODO: adaptive? depends on hardware.
        /// </summary>
        public int PassIters { get; set; } = 500;

        /// <summary>
        /// Number of iterations between resetting points.
        /// Apo/Chaotica: const 10000
        /// Zueuk: max 500 enough
        /// TODO: adaptive possible? Reset earlier if ... ?
        /// Gradually increase MaxIters? x
        /// Make it an option for high quality renders?
        /// TODO: move reset to compute shader? adaptive for each thread
        /// </summary>
        private int MaxIters { get; set; } = 1000;

        private int PassItersCnt = 0;

        /// <summary>
        /// Total iterations since accumulation reset
        /// </summary>
        private ulong IterAcc = 0;

        private bool updateDisplayNow = false;
        private bool rendering = false;


        private IGraphicsContext ctx;//add public setter?
        private IWindowInfo wInfo;//add public setter?

        //compute handlers
        private int computeProgramH;
        private int histogramH;
        private int settingsbufH;
        private int itersbufH;
        private int pointsbufH;
        private int palettebufH;
        private int tfparamsbufH;
        private int xaosbufH;
        private int last_tf_index_bufH;
        //display handlers
        private int fboH;
        private int dispTexH;
        private int displayProgramH;
        

        public RendererGL(IGraphicsContext ctx, IWindowInfo wInfo)
        {
            this.ctx = ctx;
            this.wInfo = wInfo;

            AnimationManager = new AnimationManager();

            LoadParams(new IFS(true));

            //TODO: separate opengl initialization from ctor
            initDisplay();
            initRenderer();

        }

        public void LoadParams(IFS p)
        {
            if(CurrentParams!=null)
                CurrentParams.ViewSettings.PropertyChanged -= HandleInvalidation;
            CurrentParams = p;
            CurrentParams.ViewSettings.PropertyChanged += HandleInvalidation;
            InvalidateParams();
        }

        private void HandleInvalidation(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "1":
                    InvalidateAccumulation();
                    break;
                case "2":
                    InvalidateParams();
                    break;
                case "0":
                default:
                    break;

            }
        }

        public void InvalidateAccumulation()
        {
            //can be called multiple times, but it's enough to reset once before first frame
            invalidAccumulation = true;

        }
        public void InvalidateParams()
        {
            //can be called multiple times, but it's enough to reset once before first frame
            InvalidateAccumulation();
            invalidParams = true;
        }

        public void SetRenderScale(float scale)
        {
            RenderScale = scale;

            //wait to stop

            //set uniforms
            GL.Uniform1(GL.GetUniformLocation(computeProgramH, "width"), RenderWidth);
            GL.Uniform1(GL.GetUniformLocation(computeProgramH, "height"), RenderHeight);

            GL.Viewport(0, 0, RenderWidth, RenderHeight);

            InvalidateAccumulation();

            //restart if needed

        }

        public void SetDisplayResolution(int displayWidth, int displayHeight)
        {
            this.displayWidth = displayWidth;
            this.displayHeight = displayHeight;
        }

        private void UpdatePointsState()
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, pointsbufH);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 4 * ThreadCount * sizeof(float), StartingDistributions.UniformUnitCube(ThreadCount), BufferUsageHint.DynamicDraw);
        }

        public void RenderFrame()
        {
            GL.UseProgram(computeProgramH);

            if (invalidAccumulation)
            {
                //reset accumulation
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, histogramH);
                GL.ClearNamedBufferData(histogramH, PixelInternalFormat.R32f, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
                invalidAccumulation = false;
                dispatchCnt = 0;
                IterAcc = 0;

                if (invalidParams)
                {
                    UpdatePointsState();

                    //update iterators
                    //generate iterator and transform structs
                    List<IteratorStruct> its = new List<IteratorStruct>();
                    List<float> tfsparams = new List<float>();
                    foreach (var it in CurrentParams.Iterators)
                    {
                        //iterators
                        its.Add(new IteratorStruct
                        {
                            tfId = it.Transform.Id,
                            tfParamsStart = tfsparams.Count,
                            wsum = (float)it.WeightTo.Sum(xw => xw.Value * xw.Key.baseWeight),
                            cs = (float)it.cs,
                            ci = (float)it.ci,
                            op = (float)it.op,
                        });
                        //transform params
                        List<double> tfiparams = it.Transform.GetListOfParams();
                        tfsparams.AddRange(tfiparams.Select(p => (float)p));
                    }
                    //TODO: tfparamstart pop last value

                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, itersbufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, its.Count * (4 * sizeof(int) + 4 * sizeof(float)), its.ToArray(), BufferUsageHint.DynamicDraw);

                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, tfparamsbufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, tfsparams.Count * sizeof(float), tfsparams.ToArray(), BufferUsageHint.DynamicDraw);

                    //generate flattened xaos weight matrix
                    List<float> xaosm = new List<float>(CurrentParams.Iterators.Count * CurrentParams.Iterators.Count);
                    foreach (var it in CurrentParams.Iterators)
                    {
                        foreach (var toIt in CurrentParams.Iterators)
                        {
                            if(it.WeightTo.ContainsKey(toIt))
                                xaosm.Add((float)(it.WeightTo[toIt] * toIt.baseWeight));
                            else
                                xaosm.Add(0);
                        }
                    }
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, xaosbufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, xaosm.Capacity * sizeof(float), xaosm.ToArray(), BufferUsageHint.DynamicDraw);

                    //regenerated by shader in 1st iteration
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, last_tf_index_bufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, ThreadCount * sizeof(int), IntPtr.Zero, BufferUsageHint.DynamicDraw);

                    //update palette
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, palettebufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, CurrentParams.Palette.Colors.Count * sizeof(float) * 4, CurrentParams.Palette.Colors.ToArray(), BufferUsageHint.DynamicDraw);

                    invalidParams = false;
                }
            }

            if (PassItersCnt>=MaxIters)
            {
                PassItersCnt = 0;
                UpdatePointsState();
                //idea: place new random points along the most dense area?
                //idea: place new random points along the least dense area?
            }

            var settings = new SettingsStruct
            {
                CameraBase = CurrentParams.ViewSettings.Camera.Params,
                itnum = CurrentParams.Iterators.Count,
                pass_iters = PassIters,
                dispatchCnt = dispatchCnt,
                fog_effect = (float)CurrentParams.ViewSettings.FogEffect,
                dof = (float)CurrentParams.ViewSettings.Dof,
                focusdistance = (float)CurrentParams.ViewSettings.FocusDistance,
                focusarea = (float)CurrentParams.ViewSettings.FocusArea,
                focuspoint = CurrentParams.ViewSettings.Camera.Params.position + (float)CurrentParams.ViewSettings.FocusDistance * CurrentParams.ViewSettings.Camera.Params.forward,
                palettecnt = CurrentParams.Palette.Colors.Count
            };
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, settingsbufH);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 4 * sizeof(int) + (24 + 8) * sizeof(float), ref settings, BufferUsageHint.StreamDraw);

            GL.Finish();
            GL.DispatchCompute(ThreadCount, 1, 1);

            IterAcc += Convert.ToUInt64(PassIters *ThreadCount);
            PassItersCnt += PassIters;
            dispatchCnt++;

            //GL.Finish();

            if (updateDisplayNow || UpdateDisplayOnRender)
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fboH);//
                GL.UseProgram(displayProgramH);
                //TODO:  only update if needed
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "width"), (float)RenderWidth);//displaywidth?
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "height"), (float)RenderHeight);
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "ActualDensity"), 1+(uint)(IterAcc/1000000));//apo:*0.001
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "Brightness"), (float)CurrentParams.ViewSettings.Brightness);
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "InvGamma"), (float)(1.0f/ CurrentParams.ViewSettings.Gamma));
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "GammaThreshold"), (float)CurrentParams.ViewSettings.GammaThreshold);
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "Vibrancy"), (float)CurrentParams.ViewSettings.Vibrancy);
                GL.Uniform3(GL.GetUniformLocation(displayProgramH, "BackgroundColor"), CurrentParams.ViewSettings.BackgroundColor.R / 255.0f, CurrentParams.ViewSettings.BackgroundColor.G / 255.0f, CurrentParams.ViewSettings.BackgroundColor.B / 255.0f);

                //draw quad
                GL.Begin(PrimitiveType.Quads);
                GL.Vertex2(0, 0);
                GL.Vertex2(0, 1);
                GL.Vertex2(1, 1);
                GL.Vertex2(1, 0);
                GL.End();

                float rw = displayWidth / (float)RenderWidth;
                float rh = displayHeight / (float)RenderHeight;
                float rr = (rw < rh ? rw : rh)*.98f;
                GL.BlitNamedFramebuffer(fboH,
                    0, 0, 0, RenderWidth, RenderHeight, 
                    (int)(displayWidth / 2 - RenderWidth / 2 * rr), 
                    (int)(displayHeight / 2 - RenderHeight / 2 * rr), 
                    (int)(displayWidth / 2 + RenderWidth / 2 * rr), 
                    (int)(displayHeight / 2 + RenderHeight / 2 * rr),
                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
                //GL.CopyImageSubData(dispTexH, ImageTarget.Texture2D, 1, 0, 0, 0, 0, ImageTarget.Texture2D, 1, dw / 2 - Width / 2, dh / 2 - Height / 2, dw, dh, Height, Width);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

                DisplayFrameCompleted?.Invoke(this, null);
                updateDisplayNow = false;
            }

        }

        public void StartRendering()
        {
            if (!rendering)
            {
                rendering = true;

                new System.Threading.Thread(() =>
                {
                    ctx.MakeCurrent(wInfo);
                    while (rendering)
                        RenderFrame();
                    ctx.MakeCurrent(null);
                    stopRender.Set();
                }).Start();
            }
        }

        System.Threading.AutoResetEvent stopRender = new System.Threading.AutoResetEvent(false);

        public void StopRendering()
        {
            rendering = false;
            
            GL.Finish();
        }

        public void UpdateDisplay()
        {
            updateDisplayNow = true;
            //TODO: make 1 frame, skip 1st (compute) pass
        }

        /// <summary>
        /// Pixel format: rgba
        /// </summary>
        /// <returns></returns>
        public async Task<double[,][]> GenerateImage(bool fillAlpha = true)
        {
            float[] d = new float[RenderWidth * RenderHeight * 4];//rgba

            bool continueRendering = false;
            if (rendering)
            {
                UpdateDisplay();//RenderFrame() if needed
                StopRendering();
                continueRendering = true;
            }
            stopRender.WaitOne();//wait for render thread to stop

            ctx.MakeCurrent(wInfo);//lend context from render thread
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fboH);
            GL.ReadPixels(0, 0, RenderWidth, RenderHeight, PixelFormat.Rgba, PixelType.Float, d);
            ctx.MakeCurrent(null);

            double[,][] o = new double[RenderWidth, RenderHeight][];
            await Task.Run(() =>
            {
                for (int x = 0; x < RenderWidth; x++)
                    for (int y = 0; y < RenderHeight; y++)
                    {
                        o[x, y] = new double[4];
                        o[x, y][0] = d[x * 4 + y * 4 * RenderWidth + 0];
                        o[x, y][1] = d[x * 4 + y * 4 * RenderWidth + 1];
                        o[x, y][2] = d[x * 4 + y * 4 * RenderWidth + 2];
                        o[x, y][3] = fillAlpha ? 1.0 : d[x * 4 + y * 4 * RenderWidth + 3];
                    }
            });

            //TODO: image save in netstandard?

            if (continueRendering)
                StartRendering();//restart render thread if it was running

            return o;
        }

        private void initDisplay()
        {
            var assembly = typeof(RendererGL).GetTypeInfo().Assembly;

            var vertexShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertexShader, new StreamReader(assembly.GetManifestResourceStream("IFSEngine.glsl.Display.vert.shader")).ReadToEnd());
            GL.CompileShader(vertexShader);
            GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out int status);
            if (status == 0)
            {
                throw new GraphicsException(
                    String.Format("Error compiling {0} shader: {1}", ShaderType.VertexShader.ToString(), GL.GetShaderInfoLog(vertexShader)));
            }

            var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragmentShader, new StreamReader(assembly.GetManifestResourceStream("IFSEngine.glsl.Display.frag.shader")).ReadToEnd());
            GL.CompileShader(fragmentShader);
            GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out status);
            if (status == 0)
            {
                throw new GraphicsException(
                    String.Format("Error compiling {0} shader: {1}", ShaderType.FragmentShader.ToString(), GL.GetShaderInfoLog(fragmentShader)));
            }
            
            //init display image texture
            dispTexH = GL.GenTexture();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, dispTexH);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            //GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
            //GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);

            //TODO: display resolution?
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, RenderWidth, RenderHeight, 0, PixelFormat.Rgba, PixelType.Float, new IntPtr(0));

            fboH = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fboH);//offscreen
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, dispTexH, 0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new GraphicsException("Frame Buffer Error");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);//screen

            displayProgramH = GL.CreateProgram();
            GL.AttachShader(displayProgramH, vertexShader);
            GL.AttachShader(displayProgramH, fragmentShader);
            GL.LinkProgram(displayProgramH);
            GL.GetProgram(displayProgramH, GetProgramParameterName.LinkStatus, out status);
            if (status == 0)
            {
                throw new GraphicsException(
                    String.Format("Error linking program: {0}", GL.GetProgramInfoLog(displayProgramH)));
            }

            GL.DetachShader(displayProgramH, vertexShader);
            GL.DetachShader(displayProgramH, fragmentShader);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);

            GL.UseProgram(displayProgramH);

            histogramH = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, histogramH);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, RenderWidth * RenderHeight * 4 * sizeof(float), IntPtr.Zero, BufferUsageHint.StaticCopy);

        }

        private void initRenderer()
        {

            //assemble source string
            var resource = typeof(RendererGL).GetTypeInfo().Assembly.GetManifestResourceStream("IFSEngine.glsl.ifs_kernel.compute");
            string computeShaderSource = new StreamReader(resource).ReadToEnd();

            //compile compute shader
            int computeShaderH = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(computeShaderH, computeShaderSource);
            GL.CompileShader(computeShaderH);
            Console.WriteLine(GL.GetShaderInfoLog(computeShaderH));

            //build shader program
            computeProgramH = GL.CreateProgram();
            GL.AttachShader(computeProgramH, computeShaderH);
            GL.LinkProgram(computeProgramH);
            Console.WriteLine(GL.GetProgramInfoLog(computeProgramH));
            GL.UseProgram(computeProgramH);

            //create buffers
            pointsbufH = GL.GenBuffer();
            settingsbufH = GL.GenBuffer();
            itersbufH = GL.GenBuffer();
            palettebufH = GL.GenBuffer();
            tfparamsbufH = GL.GenBuffer();
            xaosbufH = GL.GenBuffer();
            last_tf_index_bufH = GL.GenBuffer();

            //bind layout:
            GL.BindImageTexture(0, dispTexH, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba32f);//TODO: use this or remove
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, histogramH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, pointsbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, settingsbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, itersbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, palettebufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, tfparamsbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, xaosbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 8, last_tf_index_bufH);


            SetRenderScale(1.0f);
        }

        public void Dispose()
        {
            DisplayFrameCompleted = null;
            StopRendering();
            //TOOD: dispose
        }

    }
}
