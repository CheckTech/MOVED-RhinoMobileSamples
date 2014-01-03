//
// HelloRhinoView.cs
// HelloRhino.Touch
//
// Created by dan (dan@mcneel.com) on 9/19/2013
// Copyright 2013 Robert McNeel & Associates.  All rights reserved.
// OpenNURBS, Rhinoceros, and Rhino3D are registered trademarks of Robert
// McNeel & Associates.
//
// THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT EXPRESS OR IMPLIED WARRANTY.
// ALL IMPLIED WARRANTIES OF FITNESS FOR ANY PARTICULAR PURPOSE AND OF
// MERCHANTABILITY ARE HEREBY DISCLAIMED.
//
using System;
using System.Timers;

using OpenTK;
using OpenTK.Platform.iPhoneOS;
using OpenTK.Graphics.ES20;

using MonoTouch.Foundation;
using MonoTouch.CoreAnimation;
using MonoTouch.ObjCRuntime;
using MonoTouch.OpenGLES;
using MonoTouch.UIKit;

using Rhino.DocObjects;
using RhinoMobile.Display;
using RhinoMobile;
using RhinoMobile.Model;

namespace HelloRhino.Touch
{

	[Register ("HelloRhinoView")]
	public class HelloRhinoView : iPhoneOSGameView
	{
		public enum InitializationState
		{
			Uninitialized,
			Initialized,
			ErrorDuringInitialization
		}

		#region members
		//rendering fields
		int m_frameBufferWidth, m_frameBufferHeight;
		int m_frameInterval;

		// restore view animation variables
		bool m_atInitialPosition;
		bool m_startRestoreAtInitialPosition;
		bool m_inAnimatedRestoreView;
		#endregion

		#region properties
		/// <value> The renderer associated with this view. </value>
		public ES2Renderer Renderer { get; private set; }

		/// <value> InactivityTimer keeps track of how long the view has not changed at all. </value>
		private Timer InactivityTimer { get; set; }

		/// <value> OrbitDollyGestureRecognizer listens for single and two-finger pan-like events. </value>
		public OrbitDollyGestureRecognizer OrbitDollyRecognizer { get; private set; }

		/// <value> ZoomRecognizer listens for pinch gestures </value>
		public UIPinchGestureRecognizer ZoomRecognizer { get; private set; }

		/// <value> The double-tap gesture recognizer listens for double-taps. </value>
		public UITapGestureRecognizer DoubleTapGestureRecognizer { get; private set; }

		/// <value> True if this view's OpenGL state is setup </value>
		public InitializationState Initialized { get; set; }

		/// <value> DisplayLink for this view </value>
		protected CADisplayLink DisplayLink { get; set; }

		/// <value> The ViewportInfo for this view </value>
		protected ViewportInfo Viewport { get; set; }

		/// <value> The FrameBufferObject created by OpenTk framework </value>
		protected RhGLFramebufferObject VisibleFBO { get; set; }

		/// <value> The FrameBufferObject we create for Multi-Sample Anti-aliasing rendering. </value>
		protected RhGLFramebufferObject MsaaFBO { get; set; }

		/// <value> The FrameBufferObject placeholder that tracks which FBO is currently in use. </value>
		protected RhGLFramebufferObject ActiveFBO { get; set; }

		/// <value> The initial starting view position. </value>
		protected ViewportInfo InitialPosition { get; set; }

		/// <value> The last position of the Viewport before any other change. </value>
		protected ViewportInfo LastPosition { get; set; }

		/// <value> The Viewport to restore from - for tweening. </value>
		protected ViewportInfo RestoreViewStartViewport { get; set; }

		/// <value> The Viewport to restore to - for tweening </value>
		protected ViewportInfo RestoreViewFinishViewport { get; set; }

		/// <value> The startTime of the restore view action. </value>
		protected DateTime RestoreViewStartTime { get; set; }

		/// <value> The total time of the restore view action. </value>
		protected TimeSpan RestoreViewTotalTime { get; set; }

		/// <value> IsAnimating is true if the view is currently changing. </value>
		public bool IsAnimating { get; private set; }

		/// <value> FrameInterval is backed by the displayLink frameInterval. </value>
		public int FrameInterval 
		{
			get {
				return m_frameInterval;
			}

			set {
				if (value <= 0)
					throw new ArgumentException ();
				m_frameInterval = value;
				if (IsAnimating) {
					StopAnimating ();
					StartAnimating ();
				}
			}
		}
		#endregion

		#region constructors and disposal
		[Export("initWithCoder:")]
		public HelloRhinoView (NSCoder coder) : base (coder)
		{
			LayerRetainsBacking = true;
			LayerColorFormat = EAGLColorFormat.RGBA8;
			Renderer = new ES2Renderer ();

			Initialized = InitializationState.Uninitialized;

			ContentScaleFactor = UIScreen.MainScreen.Scale;

			SetupGestureRecognizers ();

			// subscribe to mesh prep events in the model...
			App.Manager.CurrentModel.MeshPrep += new MeshPreparationHandler (ObserveMeshPrep);
		}
		#endregion

		#region methods
		/// <summary>
		/// ViewDidAppear is called by CocoaTouch when the view has appeared...
		/// </summary>
		public void ViewDidAppear ()
		{
			StartInactivityTimer ();
		}

		/// <summary>
		/// StartInactivityTimer resets and restarts the inactivityTimer.
		/// </summary>
		private void StartInactivityTimer ()
		{
			if (InactivityTimer != null) {
				if (InactivityTimer.Enabled)
					InactivityTimer.Close ();
			}
		
			InactivityTimer = new System.Timers.Timer (9000);
			InactivityTimer.Elapsed += new ElapsedEventHandler (OnTimedEvent);
			InactivityTimer.AutoReset = true;
			GC.KeepAlive (InactivityTimer); // so that it doesn't get garbage collected.
			InactivityTimer.Enabled = true;
		}

		/// <summary>
		/// OnTimedEvent is called every 9 seconds (an arbitrary time) to make sure that 
		/// the graphics context is maintained when the currentModel is not being viewed.  
		/// This is to handle circumstances where the user loads and initializes a model,
		/// but the is not currently being displayed.
		/// </summary>
		private void OnTimedEvent(object source, ElapsedEventArgs e)
		{
			SetNeedsDisplay ();
		}

		/// <summary>
		/// GetLayerClass return to OpenTK the type of platform-specific layer backing.
		/// </summary>
		[Export ("layerClass")]
		public static new Class GetLayerClass ()
		{
			return iPhoneOSGameView.GetLayerClass ();
		}

		/// <summary>
		/// ConfigureLayer is called by OpenTK as part of the initialization step.
		/// </summary>
		protected override void ConfigureLayer (CAEAGLLayer eaglLayer)
		{
			eaglLayer.Opaque = true;
		}


		/// <summary>
		/// Tell opengl that we want to use OpenGL ES2 capabilities
		/// Must be done right before the frame buffer is created
		/// </summary>
		InitializationState InitializeOpenGL()
		{
			if (Initialized == InitializationState.Uninitialized) {
				ContextRenderingApi = EAGLRenderingAPI.OpenGLES2;
				base.CreateFrameBuffer ();

				//CheckGLError ();

        // Try to create a MSAA FBO that uses 8x AA and both a color and a depth buffer...
        //
        // Note: Depending on what is supported by the hardware, this might result in a
        //       lower AA. We use 8x here for starters, but the simulators only
        //       support 4x. Since we're not sure how high certain devices may go, 8x is 
        //       probably as high as we want to go for performance and resource purposes.
				MsaaFBO = new RhGLFramebufferObject ((int)(Size.Width * ContentScaleFactor), (int)(Size.Height * ContentScaleFactor), 8, true, true);

        // If the MSAA FBO is invalid, or the samples used is 0, then it means we
        // don't really have a MSAA FBO, so just nullify it.
        //
        // Note: RhGLFramebufferObject will successfully create an FBO using 0 samples
        //       since it dynamically scales down the sample count until the creation 
        //       succeeds...which means you can eventually arrive at an FBO that uses
        //       0 samples...so if you try to create a MSAA FBO and it results in an
        //       FBO that uses 0 samples, then you really haven't created a MSAA FBO.
        // Note2: The above situation will most likely only occur on hardware that does 
        //        not support MSAA.
				if (!MsaaFBO.IsValid || (MsaaFBO.ColorBuffer.SamplesUsed == 0))
        {
					MsaaFBO.Destroy ();
					MsaaFBO = null;
        }

				//CheckGLError ();
        
				// Create our visible FBO using the framework's framebuffer...
        //
        // OpenTk creates an FBO by default for its main rendering target. We simply 
        // just create one of our FBOs from its handle...and the rest of the FBO gets
        // filled in by our implementation.
        VisibleFBO = new RhGLFramebufferObject( (uint)Framebuffer );
				VisibleFBO.DepthBuffer = new RhGLRenderBuffer ((int)(Size.Width * ContentScaleFactor), (int)(Size.Height * ContentScaleFactor), All.DepthComponent16, 0);

				//CheckGLError ();

        // Note: If we do have and use a MSAA FBO, then there is no reason to create a depth buffer
        //       for the visible FBO since all we'll be doing is copying the MSAA FBO into it. This
        //       cuts down on resource usage... If we plan on using both types of FBOs at runtime,
        //       then we'll need to create a depth buffer for the visible FBO always. For not, let's
        //       only do it if we failed to create a MSAA FBO because we assume that the MSAA FBO
        //       will always be the primary rendering target.
        if (MsaaFBO != null) 
        {
          ActiveFBO = MsaaFBO;
        } 
        else 
        {
          ActiveFBO = VisibleFBO;
        }

				m_frameBufferWidth = (int)(Size.Width * ContentScaleFactor);
				m_frameBufferHeight = (int)(Size.Height * ContentScaleFactor);

				MakeCurrent ();
				Initialized = InitializationState.Initialized;

				GL.Viewport (0, 0, m_frameBufferWidth, m_frameBufferHeight);

				GL.ClearDepth (0.0f);
				GL.DepthRange (0.0f, 1.0f);
				GL.Enable (EnableCap.DepthTest);
				GL.DepthFunc (DepthFunction.Equal);
				GL.DepthMask (true);

				GL.BlendFunc (BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);
				GL.Disable (EnableCap.Dither);
				GL.Disable (EnableCap.CullFace);

				//CheckGLError ();
			}
			return Initialized;
		}

		/// <summary>
		/// OnRenderFrame is called just before DrawFrameBuffer.
		/// </summary>
		protected override void OnRenderFrame(FrameEventArgs e)
		{
			base.OnRenderFrame (e);
			DrawFrameBuffer ();
		}

		/// <summary>
		/// DrawFrameBuffer calls into the renderer to draw the frame.
		/// </summary>
		void DrawFrameBuffer()
		{
			if(InitializeOpenGL() == InitializationState.Initialized) {
				MakeCurrent ();

				Renderer.Frame = Frame;

				if (Viewport == null && App.Manager.CurrentModel.IsReadyForRendering)
					SetupViewport ();

				if (!App.Manager.FastDrawing)
					ActiveFBO = MsaaFBO;
				else
					ActiveFBO = VisibleFBO;

        // enable our active FBO...
        ActiveFBO.Enable ();

				if (m_inAnimatedRestoreView)
					AnimateRestoreView ();

				// render the model...
				Renderer.RenderModel (App.Manager.CurrentModel, Viewport);

        // copy our active FBO into our visible FBO...
        // Note: if the active and visible FBO are one in the same, then this results in a NOP.
				ActiveFBO.CopyTo (VisibleFBO);

				SwapBuffers ();
			}
		}

		/// <summary>
		/// Find the Perspective viewport in the 3dm file and sets up the default view.
		/// </summary>
		protected void SetupViewport ()
		{
			if (App.Manager.CurrentModel == null)
				return;

			Viewport = new ViewportInfo ();

			bool viewInitialized = false;
			int viewCount = App.Manager.CurrentModel.ModelFile.Views.Count;

			// find first perspective viewport projection in file
			if (viewCount > 0) {
				foreach (var view in App.Manager.CurrentModel.ModelFile.Views) {
					if (view.Viewport.IsPerspectiveProjection) {
						viewInitialized = true;
						Viewport = view.Viewport;
						Viewport.TargetPoint = view.Viewport.TargetPoint;
						break;
					}
				}
			}

			// If there isn't one, then cook up a viewport from scratch...
			if (!viewInitialized) {
				Viewport.SetScreenPort (0, m_frameBufferWidth, 0, m_frameBufferHeight, 1, 1000);
				Viewport.TargetPoint = new Rhino.Geometry.Point3d (0, 0, 0);
				var plane = new Rhino.Geometry.Plane (Rhino.Geometry.Point3d.Origin, new Rhino.Geometry.Vector3d (-1, -1, -1));
				Viewport.SetCameraLocation(new Rhino.Geometry.Point3d (10, 10, 10));
				var dir = new Rhino.Geometry.Vector3d (-1, -1, -1);
				dir.Unitize ();
				Viewport.SetCameraDirection (dir);
				Viewport.SetCameraUp (plane.YAxis);
				Viewport.SetFrustum (-1, 1, -1, 1, 0.1, 1000);
				Viewport.FrustumAspect = Viewport.ScreenPortAspect;
				Viewport.IsPerspectiveProjection = true;
				Viewport.Camera35mmLensLength = 50;
				if (App.Manager.CurrentModel != null) {
					if (App.Manager.CurrentModel.AllMeshes != null)
						Viewport.DollyExtents (App.Manager.CurrentModel.AllMeshes, 1.0);
				}
			}

			// Fix up viewport values
			var cameraDir = Viewport.CameraDirection;
			cameraDir.Unitize ();
			Viewport.SetCameraDirection (cameraDir);

			var cameraUp = Viewport.CameraUp;
			cameraUp.Unitize ();
			Viewport.SetCameraUp (cameraUp);

			ResizeViewport ();

			Renderer.Viewport = Viewport;

			// save initial viewport settings for restoreView
			InitialPosition = new ViewportInfo (Viewport);
			LastPosition = new ViewportInfo (Viewport);
			m_atInitialPosition = true;
		}

		/// <summary>
		/// Dynamically set the frustum based on the clipping
		/// </summary>
		protected void SetFrustum (ViewportInfo viewport, Rhino.Geometry.BoundingBox bBox)
		{
			ClippingInfo clipping = new ClippingInfo();
			clipping.bbox = bBox;
			if (ClippingPlanes.CalcClippingPlanes (viewport, clipping))
				ClippingPlanes.SetupFrustum (viewport, clipping);
		}

		/// <summary>
		/// Resizes the viewport on rotation
		/// </summary>
		protected void ResizeViewport ()
		{
			if (Viewport == null)
				return;

			var newRectangle = Bounds;
			Viewport.SetScreenPort (0, (int)(newRectangle.Width - 1), 0, (int)(newRectangle.Height - 1), 0, 0);

			double newPortAspect = (newRectangle.Size.Width / newRectangle.Size.Height);
			Viewport.FrustumAspect = newPortAspect;

			if (App.Manager.CurrentModel != null)
				SetFrustum (Viewport, App.Manager.CurrentModel.BBox);
		}
	
		/// <summary>
		/// LayoutSubviews is called on rotation events
		/// </summary>
		public override void LayoutSubviews ()
		{
			base.LayoutSubviews ();
			if (Initialized == InitializationState.Initialized) {

				ResizeViewport ();
			
				MakeCurrent ();

				Renderer.Resize ();

				DrawFrameBuffer ();
			}
		}

		/// <summary>
		/// DestroyFrameBuffer cleans up the framebuffer and vertexbuffer objects.
		/// </summary>
		protected override void DestroyFrameBuffer ()
		{
			if (App.Manager.CurrentModel != null) {
				MakeCurrent ();

				// Destroy all buffers on all meshes
				foreach (var obj in App.Manager.CurrentModel.DisplayObjects) {
					var mesh = obj as RhinoMobile.Display.DisplayMesh;
					if (mesh != null) {
						if (mesh.VertexBufferHandle != Globals.UNSET_HANDLE) {
							uint vbo = mesh.VertexBufferHandle;
							GL.DeleteBuffers (1, ref vbo);
							mesh.VertexBufferHandle = Globals.UNSET_HANDLE;
						}
						if (mesh.IndexBufferHandle != Globals.UNSET_HANDLE) {
							uint ibo = mesh.IndexBufferHandle;
							GL.DeleteBuffers (1, ref ibo);
							mesh.IndexBufferHandle = Globals.UNSET_HANDLE;
						}
					}
				}

				// Destroy all buffers on all transparent meshes
				foreach (var obj in App.Manager.CurrentModel.TransparentObjects) {
					var mesh = obj as RhinoMobile.Display.DisplayMesh;
					if (mesh != null) {
						if (mesh.VertexBufferHandle != Globals.UNSET_HANDLE) {
							uint vbo = mesh.VertexBufferHandle;
							GL.DeleteBuffers (1, ref vbo);
							mesh.VertexBufferHandle = Globals.UNSET_HANDLE;
						}
						if (mesh.IndexBufferHandle != Globals.UNSET_HANDLE) {
							uint ibo = mesh.IndexBufferHandle;
							GL.DeleteBuffers (1, ref ibo);
							mesh.IndexBufferHandle = Globals.UNSET_HANDLE;
						}
					}
				}

				// Destroy all the buffers
        if (MsaaFBO != null) {
          MsaaFBO.Handle = Globals.UNSET_HANDLE;
          MsaaFBO = null;
        }

        if (VisibleFBO != null) {
          VisibleFBO.Handle = Globals.UNSET_HANDLE;
          VisibleFBO = null;
        }
			}
			base.DestroyFrameBuffer ();
		}
		#endregion

		#region Gesture Handling methods
		/// <summary>
		/// All gesture recognizers' delegates are set in ModelView to
		/// ModelView (which is UIViewController), which conforms to the UIGestureRecognizerDelegate interface.
		/// The delegate is set in the viewDidLoad method.
		/// This view's owner (ModelView) receives the: ShouldRecognizeSimultaneouslyWithGestureRecognizer
		/// callback for each of its delegates.  
		/// </summary>
		private void SetupGestureRecognizers ()
		{
			// Pinch - Zoom
			ZoomRecognizer = new UIPinchGestureRecognizer (this, new Selector ("ZoomCameraWithGesture"));
			ZoomRecognizer.Enabled = false;
			AddGestureRecognizer (ZoomRecognizer);

			// Orbit & Dolly
			OrbitDollyRecognizer = new OrbitDollyGestureRecognizer ();
			OrbitDollyRecognizer.AddTarget (this, new Selector ("OrbitDollyCameraWithGesture"));
			OrbitDollyRecognizer.MaximumNumberOfTouches = 2;
			OrbitDollyRecognizer.Enabled = false;
			AddGestureRecognizer (OrbitDollyRecognizer);

			// Zoom Extents / Restore Last View
			DoubleTapGestureRecognizer = new UITapGestureRecognizer ();
			DoubleTapGestureRecognizer.AddTarget (this, new Selector ("ZoomExtentsWithGesture"));
			DoubleTapGestureRecognizer.NumberOfTapsRequired = 2;
			DoubleTapGestureRecognizer.Enabled = false;
			AddGestureRecognizer (DoubleTapGestureRecognizer);
		}

		protected void EnableAllGestureRecognizers ()
		{
			foreach (UIGestureRecognizer recognizer in GestureRecognizers)
				recognizer.Enabled = true;
		}

		protected void DisableAllGestureRecognizers ()
		{
			foreach (UIGestureRecognizer recognizer in GestureRecognizers)
				recognizer.Enabled = false;
		}

		/// <summary>
		/// ZoomExtentsWithGesture is called when a DoubleTapGesture is detected.
		/// </summary>
		[Export("ZoomExtentsWithGesture")]
		private void ZoomExtentsWithGesture (UIGestureRecognizer gesture)
		{
			if (Viewport == null)
				return;

			if (gesture.State == UIGestureRecognizerState.Ended) {
				StartInactivityTimer ();

				m_startRestoreAtInitialPosition = m_atInitialPosition;

				Rhino.DocObjects.ViewportInfo targetPosition = new ViewportInfo();

				if (m_startRestoreAtInitialPosition) {
					// animate from current position (which is initial position) back to last position
					targetPosition = LastPosition;
				} else {
					// animate from current position to initial position
					targetPosition = InitialPosition;
					LastPosition = new ViewportInfo(Viewport);
				}
			
				StartRestoreViewTo (targetPosition);
			}
		}

		/// <summary>
		/// ZoomCameraWithGesture is called in response to ZoomRecognizer events.
		/// </summary>
		[Export("ZoomCameraWithGesture")]
		private void ZoomCameraWithGesture (UIPinchGestureRecognizer gesture)
		{
			if (Viewport == null)
				return;

			if (gesture.State == UIGestureRecognizerState.Began) {
				if (InactivityTimer.Enabled)
					InactivityTimer.Close ();
			}

			if (gesture.State == UIGestureRecognizerState.Changed) {
				if (gesture.NumberOfTouches > 1) {
					App.Manager.FastDrawing = true;
					System.Drawing.PointF zoomPoint = OrbitDollyRecognizer.MidpointLocation;
					Viewport.Magnify (Bounds.Size.ToSize(), gesture.Scale, 0, zoomPoint); 
					gesture.Scale = 1.0f;
				}

				SetNeedsDisplay ();
			}

			if (gesture.State == UIGestureRecognizerState.Ended || gesture.State == UIGestureRecognizerState.Cancelled) {
				if (gesture.NumberOfTouches == 0) {
					App.Manager.FastDrawing = false;
				}

				SetNeedsDisplay ();
				StartInactivityTimer ();
			}
		}

		/// <summary>
		/// OrbitDollyCameraWithGesture is called in response to OrbitDollyRecognizer events.
		/// </summary>
		[Export("OrbitDollyCameraWithGesture")]
		private void OrbitDollyCameraWithGesture (OrbitDollyGestureRecognizer gesture)
		{
			if (Viewport == null)
				return;

			if (gesture.State == UIGestureRecognizerState.Began) {
				if (InactivityTimer.Enabled)
					InactivityTimer.Close ();

				SetNeedsDisplay ();
			}

			if (gesture.State == UIGestureRecognizerState.Changed) {
				App.Manager.FastDrawing = true;

				if (gesture.HasSingleTouch) {
					Viewport.GestureOrbit (Bounds.Size.ToSize(), gesture.AnchorLocation, gesture.CurrentLocation);
					gesture.AnchorLocation = gesture.CurrentLocation;
				}

				if (gesture.HasTwoTouches) {
					Viewport.LateralPan (gesture.StartLocation, gesture.MidpointLocation);
					gesture.StartLocation = gesture.MidpointLocation;
				}

				SetNeedsDisplay ();
				m_atInitialPosition = false;
			}

			if (gesture.State == UIGestureRecognizerState.Ended || gesture.State == UIGestureRecognizerState.Cancelled) {
				if (gesture.NumberOfTouches == 0)
					App.Manager.FastDrawing = false;

				SetNeedsDisplay ();
				StartInactivityTimer ();
			}

		}
		#endregion

		#region Animate Restore View
		/// <summary>
		/// StartRestoreViewTo is a helper method that is called by ZoomExtentsWithGesture to
		/// return the viewport back to it's "home" view.
		/// </summary>
		private void StartRestoreViewTo (Rhino.DocObjects.ViewportInfo targetPosition)
		{
			if (Viewport == null)
				return;

			m_inAnimatedRestoreView = true;
			App.Manager.FastDrawing = true;

			UserInteractionEnabled = false;
			RestoreViewStartTime = DateTime.Now;
			RestoreViewTotalTime = new TimeSpan (0, 0, 0, 0, 500);

			RestoreViewStartViewport = new ViewportInfo(Viewport); // start from current position
			RestoreViewFinishViewport = new ViewportInfo(targetPosition); // end on the target position

			// fix frustum aspect to match current screen aspect
			RestoreViewFinishViewport.FrustumAspect = Viewport.FrustumAspect;
		}

		/// <summary>
		/// Tween from original view to 
		/// </summary>
		private void AnimateRestoreView ()
		{
			var restoreViewCurrentTime = DateTime.Now;																				

			var currentTime = restoreViewCurrentTime;																						
			var startTime = RestoreViewStartTime;																							
			var timeElapsed = currentTime.Subtract (startTime);																	
			var timeElapsedInMs = timeElapsed.TotalMilliseconds;																
			var totalTimeOfAnimationInMs = RestoreViewTotalTime.TotalMilliseconds;						
			double percentCompleted = timeElapsedInMs / totalTimeOfAnimationInMs;

			if (percentCompleted > 1) {
				// Animation is completed. Perform one last draw.
				percentCompleted = 1;
				m_inAnimatedRestoreView = false;
				UserInteractionEnabled = true;
				m_atInitialPosition = !m_startRestoreAtInitialPosition;
			}

			// Get some data from the starting view
			Rhino.Geometry.Point3d sourceTarget = RestoreViewStartViewport.TargetPoint;
			Rhino.Geometry.Point3d sourceCamera = RestoreViewStartViewport.CameraLocation;
			double sourceDistance = sourceCamera.DistanceTo (sourceTarget);
			Rhino.Geometry.Vector3d sourceUp = RestoreViewStartViewport.CameraUp;
			sourceUp.Unitize ();

			// Get some data from the ending view
			Rhino.Geometry.Point3d targetTarget = RestoreViewFinishViewport.TargetPoint;
			Rhino.Geometry.Point3d targetCamera = RestoreViewFinishViewport.CameraLocation;
			double targetDistance = targetCamera.DistanceTo (targetTarget);
			Rhino.Geometry.Vector3d targetCameraDir = targetCamera - targetTarget;
			Rhino.Geometry.Vector3d targetUp = RestoreViewFinishViewport.CameraUp;
			targetUp.Unitize ();

			// Adjust the target camera location so that the starting camera to target distance
			// and the ending camera to target distance are the same.  Doing this will calculate
			// a constant rotational angular momentum when tweening the camera location.
			// Further down we independently tween the camera to target distance.
			targetCameraDir.Unitize ();
			targetCameraDir *= sourceDistance;
			targetCamera = targetCameraDir + targetTarget;

			// calculate interim viewport values
			double frameDistance = ViewportInfoExtensions.CosInterp(sourceDistance, targetDistance, percentCompleted);

			Rhino.Geometry.Point3d frameTarget = new Rhino.Geometry.Point3d();

			frameTarget.X = ViewportInfoExtensions.CosInterp(sourceTarget.X, targetTarget.X, percentCompleted);
			frameTarget.Y = ViewportInfoExtensions.CosInterp(sourceTarget.Y, targetTarget.Y, percentCompleted);
			frameTarget.Z = ViewportInfoExtensions.CosInterp(sourceTarget.Z, targetTarget.Z, percentCompleted);

			var origin = Rhino.Geometry.Point3d.Origin;
			Rhino.Geometry.Point3d frameCamera = origin + (ViewportInfoExtensions.Slerp((sourceCamera - origin), (targetCamera - origin), percentCompleted));
			Rhino.Geometry.Vector3d frameCameraDir = frameCamera - frameTarget;

			// adjust the camera location along the camera direction vector to preserve the target location and the camera distance
			frameCameraDir.Unitize();
			frameCameraDir *= frameDistance;
			frameCamera = frameCameraDir + frameTarget;

			Rhino.Geometry.Vector3d frameUp = new Rhino.Geometry.Vector3d(ViewportInfoExtensions.Slerp (sourceUp, targetUp, percentCompleted));

			if (percentCompleted >= 1) {
				// put the last redraw at the exact end point to eliminate any rounding errors
				Viewport.SetTarget (RestoreViewFinishViewport.TargetPoint, RestoreViewFinishViewport.CameraLocation, RestoreViewFinishViewport.CameraUp);
			} else {	
				Viewport.SetTarget (frameTarget, frameCamera, frameUp);
			}

			SetFrustum (Viewport, App.Manager.CurrentModel.BBox);

			SetNeedsDisplay ();

			if (!m_inAnimatedRestoreView) {
				// App.Manager.FastDrawing is still enabled and we just scheduled a draw of the model at the final location.
				// This entirely completes the animation. Now schedule one more redraw of the model with FastDrawing disabled
				// and this redraw will be done at exactly the same postion.  This prevents the final animation frame
				// from jumping to the final location because the final draw will take longer with FastDrawing disabled.
				PerformSelector (new Selector ("RedrawDetailed"), null, 0.05);
			}
		} 

		/// <summary>
		/// Redraw the final frame of an animation in "slow drawing" mode
		/// </summary>
		[Export("RedrawDetailed")]
		private void RedrawDetailed ()
		{
			App.Manager.FastDrawing = false;
			SetNeedsDisplay ();
		}
		#endregion

		#region DisplayLink support
		/// <summary>
		/// StartAnimating is called by DisplayLink
		/// </summary>
		public void StartAnimating ()
		{
			if (IsAnimating)
				return;

			CADisplayLink displayLink = UIScreen.MainScreen.CreateDisplayLink (this, new Selector ("drawFrame"));
			displayLink.FrameInterval = m_frameInterval;
			displayLink.AddToRunLoop (NSRunLoop.Current, NSRunLoop.NSDefaultRunLoopMode);
			this.DisplayLink = displayLink;

			IsAnimating = true;
		}

		/// <summary>
		/// StopAnimating is called by DisplayLink
		/// </summary>
		public void StopAnimating ()
		{
			if (!IsAnimating)
				return;
			DisplayLink.Invalidate ();
			DisplayLink = null;
			DestroyFrameBuffer ();
			IsAnimating = false;
		}

		/// <summary>
		/// DrawFrame is called by DisplayLink and calls OnRenderFrame in response to FrameEvents
		/// </summary>
		[Export ("drawFrame")]
		void DrawFrame ()
		{
			OnRenderFrame (new FrameEventArgs ());
		}
		#endregion

		#region Model Initialization Events
		private void ObserveMeshPrep (RMModel model, MeshPreparationProgress progress)
		{
			// Success
			if (progress.PreparationDidSucceed) {
				this.InvokeOnMainThread (delegate {
					EnableAllGestureRecognizers ();
					App.Manager.CurrentModel.MeshPrep -= new MeshPreparationHandler (ObserveMeshPrep);
				});
			}
		}
		#endregion

		#region Utilities
		/// <summary>
		/// DEBUG only.
		/// <para>Checks for outstanding GL Errors and logs them to console.</para>
		/// </summary>
		public static void CheckGLError () 
		{
			#if DEBUG 
			#if __IOS__
			var err = GL.GetError ();
			do {
				if (err != ErrorCode.NoError)
					Console.WriteLine ("GL Error: {0}", err.ToString ());
				err = GL.GetError ();
			} while ((err != ErrorCode.NoError));
			#endif
			#endif
		}
		#endregion
	
	}
}