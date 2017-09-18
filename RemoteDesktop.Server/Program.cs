﻿using RemoteDesktop.Core;
using RemoteDesktop.Server.Properties;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using WindowsInput;
using WindowsInput.Native;

namespace RemoteDesktop.Server
{
	static class Program
	{
		[STAThread]
		static void Main(string[] args)
		{
			// parse args
			bool isDebugMode = false;
			int port = 8888;
			foreach (var arg in args)
			{
				if (arg == "debug") isDebugMode = true;
				else if (arg.StartsWith("port=")) port = int.Parse(arg.Split('=')[1]);
			}

			// start app
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			if (isDebugMode) Application.Run(new MainForm());
			else Application.Run(new MainApplicationContext(port));
		}
	}

	public class MainApplicationContext : ApplicationContext
	{
		private bool isDisposed;
		private NotifyIcon trayIcon;
		private NetworkDiscovery networkDiscovery;
		private DataSocket socket;

		private Rectangle screenRect;
		private Bitmap bitmap;
		private Graphics graphics;
		PixelFormat format = PixelFormat.Format24bppRgb;
		int screenIndex;
		bool compress;
		private Timer timer;
		private Dispatcher dispatcher;

		private InputSimulator input;
		private byte inputLastMouseState, inputLastKeyboardState;

		public MainApplicationContext(int port)
		{
			// init tray icon
			var menuItems = new MenuItem[]
			{
				new MenuItem("Exit", Exit),
			};
			
			trayIcon = new NotifyIcon()
			{
				Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
				ContextMenu = new ContextMenu(menuItems),
				Visible = true
			};

			// init input simulation
			input = new InputSimulator();

			// star socket
			dispatcher = Dispatcher.CurrentDispatcher;
			socket = new DataSocket(NetworkTypes.Server);
			socket.ConnectedCallback += Socket_ConnectedCallback;
			socket.DisconnectedCallback += Socket_DisconnectedCallback;
			socket.ConnectionFailedCallback += Socket_ConnectionFailedCallback;
			socket.DataRecievedCallback += Socket_DataRecievedCallback;
			socket.StartDataRecievedCallback += Socket_StartDataRecievedCallback;
			socket.EndDataRecievedCallback += Socket_EndDataRecievedCallback;
			socket.Listen(IPAddress.Any, port);

			// start network discovery
			networkDiscovery = new NetworkDiscovery(NetworkTypes.Server);
			networkDiscovery.Register("SimpleRemoteDesktop", port);
		}

		void Exit(object sender, EventArgs e)
		{
			// dispose
			lock (this)
			{
				isDisposed = true;

				if (timer != null)
				{
					timer.Stop();
					timer.Dispose();
					timer = null;
				}

				if (networkDiscovery != null)
				{
					networkDiscovery.Dispose();
					networkDiscovery = null;
				}

				if (socket != null)
				{
					socket.Dispose();
					socket = null;
				}

				if (graphics != null)
				{
					graphics.Dispose();
					graphics = null;
				}

				if (bitmap != null)
				{
					bitmap.Dispose();
					bitmap = null;
				}
			}

			// exit
			trayIcon.Visible = false;
			Application.Exit();
		}

		private void Socket_StartDataRecievedCallback(MetaData metaData)
		{
			lock (this)
			{
				if (isDisposed) return;

				// update settings
				if (metaData.type == MetaDataTypes.UpdateSettings || metaData.type == MetaDataTypes.StartCapture)
				{
					DebugLog.Log("Updating settings");
					format = metaData.format;
					screenIndex = metaData.screenIndex;
					compress = metaData.compressed;
				}
				
				// start / stop
				if (metaData.type == MetaDataTypes.StartCapture)
				{
					dispatcher.InvokeAsync(delegate()
					{
						if (timer == null)
						{
							timer = new Timer();
							timer.Interval = 1000 / 15;
							timer.Tick += Timer_Tick;
						}
					
						timer.Start();
					});
				}
				else if (metaData.type == MetaDataTypes.PauseCapture)
				{
					dispatcher.InvokeAsync(delegate()
					{
						timer.Stop();
					});
				}
				else if (metaData.type == MetaDataTypes.ResumeCapture)
				{
					dispatcher.InvokeAsync(delegate()
					{
						timer.Start();
					});
				}
				else if (metaData.type == MetaDataTypes.UpdateMouse)
				{
					// mouse pos
					Cursor.Position = new Point(metaData.mouseX, metaData.mouseY);
					
					// mouse clicks
					if (inputLastMouseState != metaData.mouseButtonPressed)
					{
						// handle state changes
						if (inputLastMouseState == 1) input.Mouse.LeftButtonUp();
						else if (inputLastMouseState == 2) input.Mouse.RightButtonUp();
						else if (inputLastMouseState == 3) input.Mouse.XButtonUp(2);

						// handle new state
						if (metaData.mouseButtonPressed == 1) input.Mouse.LeftButtonDown();
						else if (metaData.mouseButtonPressed == 2) input.Mouse.RightButtonDown();
						else if (metaData.mouseButtonPressed == 3) input.Mouse.XButtonDown(2);
					}

					// mouse scroll wheel
					if (metaData.mouseScroll != 0) input.Mouse.VerticalScroll(metaData.mouseScroll);

					// keyboard presses
					//if (inputLastKeyboardState != metaData.keyCode)
					{
						// handle state changes
						if (metaData.keyCode != 0) input.Keyboard.KeyPress(VirtualKeyCode.VK_A);
					}
					
					// finish
					inputLastMouseState = metaData.mouseButtonPressed;
					inputLastKeyboardState = metaData.keyCode;
				}
			}
		}

		private void Socket_EndDataRecievedCallback()
		{
			// do nothing
		}

		private void Socket_DataRecievedCallback(byte[] data, int dataSize, int offset)
		{
			// do nothing
		}

		private void Socket_ConnectionFailedCallback(string error)
		{
			DebugLog.LogError("Failed to connect: " + error);
		}

		private void Socket_ConnectedCallback()
		{
			DebugLog.Log("Connected to client");
		}

		private void Socket_DisconnectedCallback()
		{
			DebugLog.Log("Disconnected from client");
			dispatcher.InvokeAsync(delegate()
			{
				if (timer != null) timer.Stop();
				socket.ReListen();
			});
		}

		private void Timer_Tick(object sender, EventArgs e)
		{
			lock (this)
			{
				if (isDisposed) return;

				CaptureScreen();
				socket.SendImage(bitmap, screenIndex, compress);
			}
		}

		private void CaptureScreen()
		{
			if (bitmap == null || bitmap.PixelFormat != format)
			{
				// get screen to catpure
				var screens = Screen.AllScreens;
				var screen = (screenIndex < screens.Length) ? screens[screenIndex] : screens[0];
				screenRect = screen.Bounds;

				// create bitmap resources
				if (bitmap != null) bitmap.Dispose();
				if (graphics != null) graphics.Dispose();
				bitmap = new Bitmap(screenRect.Width, screenRect.Height, format);
				graphics = Graphics.FromImage(bitmap);
			}

			// capture screen
			graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size);
		}
	}
}
