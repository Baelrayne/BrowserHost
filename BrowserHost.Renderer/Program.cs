﻿using BrowserHost.Common;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BrowserHost.Renderer
{
	class Program
	{
		private static string cefAssemblyDir;
		private static string dalamudAssemblyDir;

		private static Thread parentWatchThread;
		private static EventWaitHandle waitHandle;

		private static IpcBuffer<DownstreamIpcRequest, UpstreamIpcRequest> ipcBuffer;

		private static ConcurrentDictionary<Guid, Inlay> inlays = new ConcurrentDictionary<Guid, Inlay>();

		static void Main(string[] rawArgs)
		{
			Console.WriteLine("Render process running.");
			var args = RenderProcessArguments.Deserialise(rawArgs[0]);

			// Need to pull these out before Run() so the resolver can access.
			cefAssemblyDir = args.CefAssemblyDir;
			dalamudAssemblyDir = args.DalamudAssemblyDir;

			AppDomain.CurrentDomain.AssemblyResolve += CustomAssemblyResolver;

			Run(args);
		}

		// Main process logic. Seperated to ensure assembly resolution is configured.
		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void Run(RenderProcessArguments args)
		{
			waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, args.KeepAliveHandleName);

			// Boot up a thread to make sure we shut down if parent dies
			parentWatchThread = new Thread(WatchParentStatus);
			parentWatchThread.Start(args.ParentPid);

#if DEBUG
			AppDomain.CurrentDomain.FirstChanceException += (obj, e) => Console.Error.WriteLine(e.Exception.ToString());
#endif

			// Set up an instance of lumina for the renderer to use
			// TODO: Default language handling. Likely should reflect in plugin for that info.
			var lumina = new Lumina.Lumina(args.SqpackDataDir);

			// Initialise the rendering logic
			DxHandler.Initialise(args.DxgiAdapterLuid);
			CefHandler.Initialise(cefAssemblyDir, lumina);

			ipcBuffer = new IpcBuffer<DownstreamIpcRequest, UpstreamIpcRequest>(args.IpcChannelName, HandleIpcRequest);

			Console.WriteLine("Waiting...");

			waitHandle.WaitOne();
			waitHandle.Dispose();

			Console.WriteLine("Render process shutting down.");

			ipcBuffer.Dispose();

			DxHandler.Shutdown();
			CefHandler.Shutdown();

			parentWatchThread.Abort();
		}

		private static void WatchParentStatus(object pid)
		{
			Console.WriteLine($"Watching parent PID {pid}");
			var process = Process.GetProcessById((int)pid);
			process.WaitForExit();
			waitHandle.Set();

			var self = Process.GetCurrentProcess();
			self.WaitForExit(1000);
			try { self.Kill(); }
			catch (InvalidOperationException) { }
		}

		private static object HandleIpcRequest(DownstreamIpcRequest request)
		{
			switch (request)
			{
				case NewInlayRequest newInlayRequest:
				{
					// TODO: Move bulk of this into a method
					var inlay = new Inlay(newInlayRequest.Url, new Size(newInlayRequest.Width, newInlayRequest.Height));
					inlay.Initialise();
					inlays.AddOrUpdate(newInlayRequest.Guid, inlay, (guid, oldInlay) => inlay);
					inlay.CursorChanged += (sender, cursor) =>
					{
						ipcBuffer.RemoteRequest<object>(new SetCursorRequest()
						{
							Guid = newInlayRequest.Guid,
							Cursor = cursor
						});
					};
					return new TextureHandleResponse() { TextureHandle = inlay.SharedTextureHandle };
				}

				case ResizeInlayRequest resizeInlayRequest:
				{
					var inlay = inlays[resizeInlayRequest.Guid];
					if (inlay == null) { return null; }
					inlay.Resize(new Size(resizeInlayRequest.Width, resizeInlayRequest.Height));
					return new TextureHandleResponse() { TextureHandle = inlay.SharedTextureHandle };
				}

				case NavigateInlayRequest navigateInlayRequest:
				{
					var inlay = inlays[navigateInlayRequest.Guid];
					inlay.Navigate(navigateInlayRequest.Url);
					return null;
				}

				case DebugInlayRequest debugInlayRequest:
				{
					var inlay = inlays[debugInlayRequest.Guid];
					inlay.Debug();
					return null;
				}

				case EventInlayRequest eventInlayRequest:
				{
					var inlay = inlays[eventInlayRequest.Guid];
					inlay.Send(eventInlayRequest.Name, eventInlayRequest.Data);
					return null;
				}

				case RemoveInlayRequest removeInlayRequest:
				{
					var inlay = inlays[removeInlayRequest.Guid];
					inlays.TryRemove(removeInlayRequest.Guid, out Inlay _);
					inlay.Dispose();
					return null;
				}

				case MouseEventRequest mouseMoveRequest:
				{
					var inlay = inlays[mouseMoveRequest.Guid];
					inlay?.HandleMouseEvent(mouseMoveRequest);
					return null;
				}

				case KeyEventRequest keyEventRequest:
				{
					var inlay = inlays[keyEventRequest.Guid];
					inlay?.HandleKeyEvent(keyEventRequest);
					return null;
				}

				default:
					throw new Exception($"Unknown IPC request type {request.GetType().Name} received.");
			}
		}

		private static Assembly CustomAssemblyResolver(object sender, ResolveEventArgs args)
		{
			var assemblyName = args.Name.Split(new[] { ',' }, 2)[0] + ".dll";

			// TODO: Cache this?
			var dalamudAssemblies = Directory.GetFiles(dalamudAssemblyDir, "*.dll", SearchOption.TopDirectoryOnly)
				.Select(path => Path.GetFileName(path));

			string assemblyPath = null;
			if (assemblyName.StartsWith("CefSharp"))
			{
				assemblyPath = Path.Combine(cefAssemblyDir, assemblyName);
			}
			else if (dalamudAssemblies.Any(name => name == assemblyName))
			{
				assemblyPath = Path.Combine(dalamudAssemblyDir, assemblyName);
			}

			if (assemblyPath == null) { return null; }

			if (!File.Exists(assemblyPath))
			{
				Console.Error.WriteLine($"Could not find assembly `{assemblyName}` at search path `{assemblyPath}`");
				return null;
			}

			return Assembly.LoadFile(assemblyPath);
		}
	}
}
