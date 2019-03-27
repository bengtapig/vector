﻿using Anki.Vector.ExternalInterface;
using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Anki.Vector.ExternalInterface.ExternalInterface;
using AutoMapper;

namespace Vector
{
	public class Robot : IDisposable
	{
		static bool _mappingInit;
		internal ExternalInterfaceClient Client { get; private set; }
		Channel _channel;
		int _actionTagID;
		IRobotConnectionInfoStorage _connectionStorage;

		public RobotAudio Audio { get; }
		public RobotMotors Motors { get; }
		public RobotAnimation Animation { get; }
		public RobotScreen Screen { get; }

		public event EventHandler<EventArgs> OnAnyEvent;
		public event EventHandler<WakeWordEventArgs> OnWakeWord;
		public event EventHandler<RobotStateEventArgs> OnStateChanged;
		public event EventHandler<SuppressPersonalityEventArgs> OnSuppressPersonality;

		public Robot(IRobotConnectionInfoStorage connectionStorage = null)
		{
			//setup entity mapping
			if (!_mappingInit)
			{
				Mapper.Initialize(i =>
				{
					i.CreateMap<BatteryStateResponse, BatteryState>();
					i.CreateMap<NetworkStateResponse, NetworkState>();
					i.CreateMap<VersionStateResponse, VersionState>();
					i.CreateMap<Anki.Vector.ExternalInterface.WakeWord, WakeWord>()
						.ForMember(d => d.IntentHeard, m => m.MapFrom(s => s.WakeWordEnd.IntentHeard))
						.ForMember(d => d.IntentJson, m => m.MapFrom(s => s.WakeWordEnd.IntentJson))
						.ForMember(d => d.Begin, m => m.MapFrom(s => s.WakeWordBegin != null));
					i.CreateMap<RobotState, Anki.Vector.ExternalInterface.RobotState>();
				});
				_mappingInit = true;
			}

			//set fields
			_connectionStorage = connectionStorage ?? new RobotConnectionInfoStorage();
			Audio = new RobotAudio(this);
			Motors = new RobotMotors(this);
			Animation = new RobotAnimation(this);
			Screen = new RobotScreen(this);
		}

		/// <summary>
		/// use IRobotConnectionInfoStorage to retreave connection information to connect to your robot 
		/// </summary>
		/// <param name="robotName">Find your robot name (ex. Vector-A1B2) by placing Vector on the charger and double-clicking Vector's backpack button.</param>
		/// <param name="ipAddress">Update your robots IP address, otherwise leave blank.  Find your robot ip address (ex. 192.168.42.42) by placing Vector on the charger, double-clicking Vector's backpack button, then raising and lowering his arms.If you see XX.XX.XX.XX on his face, reconnect Vector to your WiFi using the Vector Companion App.</param>
		public async Task ConnectAsync(string robotName, string ipAddress = null)
		{
			if (Client == null)
			{
				//get connection info
				var connectionInfo = _connectionStorage.Get(robotName);
				if (connectionInfo == null)
				{
					throw new MissingConnectionException("No Connection Info found. (call GrantApiAccessAsync first).  If this is the first time you have connected, you must grant access for this device to communicate with Vector.");
				}

				//connect
				await CreateClientConnection(connectionInfo, ipAddress ?? connectionInfo.IpAddress);

				//update the IP address
				if (ipAddress != null && connectionInfo.IpAddress != ipAddress)
				{
					connectionInfo.IpAddress = ipAddress;
					_connectionStorage.Save(connectionInfo);
				}
			}
		}

		/// <summary>
		/// connect to your robot by passing in a connectionInfo
		/// </summary>
		/// <param name="connectionInfo">call ApiAccess.Grant() to generate a connectionInfo.  Once access is granted the conncetionInfo should be reused.</param>
		/// <returns></returns>
		public async Task ConnectAsync(RobotConnectionInfo connectionInfo)
		{
			if (Client == null)
			{
				await CreateClientConnection(connectionInfo, connectionInfo.IpAddress);
			}
		}

		/// <summary>
		/// Required for first time use.  Used to grant this device access to a Vector robot.  The robot must be joined to the wifi network using the Vector Companion App before running this.  Once access is granted you can connect via robotName
		/// </summary>
		/// <param name="robotName">Find your robot name (ex. Vector-A1B2) by placing Vector on the charger and double-clicking Vector's backpack button.</param>
		/// <param name="ipAddress">Find your robot ip address (ex. 192.168.42.42) by placing Vector on the charger, double-clicking Vector's backpack button, then raising and lowering his arms.If you see XX.XX.XX.XX on his face, reconnect Vector to your WiFi using the Vector Companion App.</param>
		/// <param name="serialNumber">Please find your robot serial number (ex. 00e20100) located on the underside of Vector, or accessible from Vector's debug screen.</param>
		/// <param name="userName">Enter your email. Make sure to use the same account that was used to set up your Vector through the Companion app.</param>
		/// <param name="password">Enter your password. Make sure to use the same account that was used to set up your Vector through the Companion app.</param>
		public async Task GrantApiAccessAsync(string robotName, string ipAddress, string serialNumber, string userName, string password)
		{
			//grant API access
			var connectionInfo = await ApiAccess.GrantAsync(robotName, ipAddress, serialNumber, userName, password);

			//save the connection info
			_connectionStorage.Save(connectionInfo);
		}

		async Task CreateClientConnection(RobotConnectionInfo connectionInfo, string ipAddress)
		{
			//create channel
			var ssl = new SslCredentials(connectionInfo.Certificate);
			var interceptor = new AsyncAuthInterceptor((context, metadata) =>
			{
				metadata.Add("authorization", $"Bearer {connectionInfo.Token}");
				return Task.CompletedTask;
			});
			var cred = ChannelCredentials.Create(ssl, CallCredentials.FromInterceptor(interceptor));
			_channel = new Channel(ipAddress, 443, cred, new ChannelOption[] { new ChannelOption("grpc.ssl_target_name_override", connectionInfo.RobotName) });

			//connect to client
			try
			{
				await _channel.ConnectAsync(DateTime.UtcNow.AddSeconds(10));
			}
			catch (TaskCanceledException ex)
			{
				throw new TimeoutException("could not connect to vector.  insure IP address is correct", ex);
			}

			//create client
			Client = new ExternalInterfaceClient(_channel);
		}

		AsyncAuthInterceptor GetAsyncAuthInterceptorFromAccessToken(string token)
		{
			return new AsyncAuthInterceptor((context, metadata) =>
			{
				metadata.Add("authorization", $"Bearer {token}");
				return Task.CompletedTask;
			});
		}

		public async Task DisconnectAsync()
		{
			if (Client != null)
			{
				await _channel.ShutdownAsync();
				Client = null;
			}
		}

		public void Dispose()
		{
			DisconnectAsync().Wait();
		}

		internal int GetActionTagID()
		{
			if (_actionTagID == (int)ActionTagConstants.InvalidSdkTag)
				_actionTagID = (int)ActionTagConstants.FirstSdkTag;
			else
				_actionTagID++;
			if (_actionTagID > (int)ActionTagConstants.LastSdkTag)
				_actionTagID = (int)ActionTagConstants.FirstSdkTag;
			return _actionTagID;
		}

		public async Task<BatteryState> GetBatteryStateAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			var result = await Client.BatteryStateAsync(new BatteryStateRequest(), cancellationToken: cancellationToken);
			if (result?.Status?.Code == ResponseStatus.Types.StatusCode.ResponseReceived)
				return Mapper.Map<BatteryState>(result);
			throw new VectorCommunicationException($"communication error: {result?.Status?.Code}");
		}

		[Obsolete("doesn't appear fully implimented yet")]
		public async Task<NetworkState> GetNetworkStateAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			var result = await Client.NetworkStateAsync(new NetworkStateRequest(), cancellationToken: cancellationToken);
			if (result?.Status?.Code == ResponseStatus.Types.StatusCode.ResponseReceived)
				return Mapper.Map<NetworkState>(result);
			throw new VectorCommunicationException($"communication error: {result?.Status?.Code}");
		}

		public async Task<VersionState> GetVersionStateAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			var result = await Client.VersionStateAsync(new VersionStateRequest(), cancellationToken: cancellationToken);
			if (result?.Status?.Code == ResponseStatus.Types.StatusCode.ResponseReceived)
				return Mapper.Map<VersionState>(result);
			throw new VectorCommunicationException($"communication error: {result?.Status?.Code}");
		}

		public async Task StartEventListeningAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			var stream = Client.EventStream(new EventRequest() { ConnectionId = Guid.NewGuid().ToString() });
			while (await stream.ResponseStream.MoveNext(cancellationToken))
			{
				var result = stream.ResponseStream.Current;

				//fire event
				switch (result.Event.EventTypeCase)
				{
					case Event.EventTypeOneofCase.TimeStampedStatus:
						break;
					case Event.EventTypeOneofCase.WakeWord:
						var e1 = new WakeWordEventArgs() { Data = Mapper.Map<WakeWord>(result.Event.WakeWord) };
						OnWakeWord?.Invoke(this, e1);
						OnAnyEvent?.Invoke(this, e1);
						break;
					case Event.EventTypeOneofCase.RobotObservedFace:
						break;
					case Event.EventTypeOneofCase.RobotChangedObservedFaceId:
						break;
					case Event.EventTypeOneofCase.ObjectEvent:
						break;
					case Event.EventTypeOneofCase.StimulationInfo:
						break;
					case Event.EventTypeOneofCase.PhotoTaken:
						break;
					case Event.EventTypeOneofCase.RobotState:
							var e2 = new RobotStateEventArgs() { Data = Mapper.Map<RobotState>(result.Event.RobotState) };
							OnStateChanged?.Invoke(this, e2);
							OnStateChanged?.Invoke(this, e2);
						break;
					case Event.EventTypeOneofCase.CubeBattery:
						break;
					case Event.EventTypeOneofCase.KeepAlive:
						break;
					case Event.EventTypeOneofCase.ConnectionResponse:
						break;
					case Event.EventTypeOneofCase.MirrorModeDisabled:
						break;
					case Event.EventTypeOneofCase.VisionModesAutoDisabled:
						break;
				}
			}
		}

		public async Task SuppressPersonalityAsync(bool overrideSafty = false, CancellationToken cancellationToken = default(CancellationToken))
		{
			var stream = Client.BehaviorControl();
			var priority = overrideSafty ? ControlRequest.Types.Priority.OverrideAll : ControlRequest.Types.Priority.TopPriorityAi;
			await stream.RequestStream.WriteAsync(new BehaviorControlRequest() { ControlRequest = new ControlRequest() { Priority = priority } });
			while(await stream.ResponseStream.MoveNext(cancellationToken))
			{
				var result = stream.ResponseStream.Current;
				if (result.ControlLostEvent != null)
				{
					OnSuppressPersonality?.Invoke(this, new SuppressPersonalityEventArgs() { IsSuppressed = false });
					await stream.RequestStream.WriteAsync(new BehaviorControlRequest() { ControlRequest = new ControlRequest() { Priority = priority } });
				}
				else if (result.ControlGrantedResponse != null)
					OnSuppressPersonality?.Invoke(this, new SuppressPersonalityEventArgs() { IsSuppressed = true });
			}
		}

		//public async Task ChangeBehaviorAsync(float frequency = .5f, CancellationToken cancellationToken = default(CancellationToken))
		//{
		//	var stream = Client.NavMapFeed(new NavMapFeedRequest() { Frequency = frequency });
		//	while (await stream.ResponseStream.MoveNext(cancellationToken))
		//	{
		//		var result = stream.ResponseStream.Current;
		//		if (result. != null)
		//			await stream.RequestStream.WriteAsync(new BehaviorControlRequest() { ControlRequest = new ControlRequest() { Priority = (ControlRequest.Types.Priority)500 } });
		//	}
		//}



		//public async Task Test2()
		//{
		//	var p = await Client.PhotosInfoAsync(new PhotosInfoRequest());
		//	var r = p.PhotoInfos.ToList();
		//	var p1 = await Client.PhotoAsync(new PhotoRequest() { PhotoId = 3 });
		//	using (var f = System.IO.File.Create(@"C:\Projects\RocDemo\RocClient\test.jpg"))
		//		p1.Image.WriteTo(f);

		//}

		//public async Task Test()
		//{
		//	var p = await Client.VersionStateAsync(new VersionStateRequest());
		//	var p2 = await Client.ProtocolVersionAsync(new ProtocolVersionRequest());
		//	var p3 = await Client.ListAnimationsAsync(new ListAnimationsRequest());
		//	//var p4 = await _client.DisplayFaceImageRGBAsync(new DisplayFaceImageRGBRequest() { DurationMs = 5000, InterruptRunning = true, FaceData = Google.Protobuf.ByteString.CopyFrom(System.IO.File.ReadAllBytes(@"C:\Projects\RocDemo\RocClient\test.jpg")) });
		//	var id = Guid.NewGuid().ToString();
		//	var tt = new EventRequest() { ConnectionId = id };
		//	var c = new CancellationTokenSource();
		//	var p5 = Client.EventStream(tt, cancellationToken: c.Token);
		//	while (await p5.ResponseStream.MoveNext())
		//	{
		//		var d = p5.ResponseStream.Current;
		//		var data = d.Event?.RobotState?.ProxData;
		//		if (data != null)
		//			Console.WriteLine(data);
		//		//c.Cancel();
		//	}
		//}

		//public async Task Test4()
		//{
		//	var c = new CancellationTokenSource();
		//	var p = Client.NavMapFeed(new NavMapFeedRequest() { Frequency = 0.5f, }, cancellationToken: c.Token);
		//	while (await p.ResponseStream.MoveNext())
		//	{
		//		var d = p.ResponseStream.Current;
		//	}
		//}

		//public async Task Test5()
		//{
		//	var p2 = Client.AssumeBehaviorControl(new BehaviorControlRequest() { ControlRequest = new ControlRequest() { Priority = ControlRequest.Types.Priority.OverrideAll } });
		//	while (await p2.ResponseStream.MoveNext())
		//	{
		//		var d = p2.ResponseStream.Current;
		//	}
		//	//var p = _client.BehaviorControl();
		//	//while(await p.ResponseStream.MoveNext())
		//	//{
		//	//	var d = p.ResponseStream.Current;
		//	//}
		//}

		//public async Task Test6()
		//{
		//	var p2 = Client.CameraFeed(new CameraFeedRequest());
		//	while (await p2.ResponseStream.MoveNext())
		//	{
		//		var d = p2.ResponseStream.Current;
		//	}

		//	var p = Client.AudioFeed(new AudioFeedRequest());
		//	while (await p.ResponseStream.MoveNext())
		//	{
		//		var d = p.ResponseStream.Current;
		//	}
		//}

		//public async Task Test7()
		//{
		//	//var p = _client.PlayAnimationAsync(new PlayAnimationRequest() {  Animation = new Animation() { Name =  } })
		//}

		
	}


	public class AnyEventArgs : EventArgs
	{
		public object Data { get; set; }
	}
	public class WakeWordEventArgs : EventArgs
	{
		public WakeWord Data { get; set; }
	}
	public class RobotStateEventArgs : EventArgs
	{
		public RobotState Data { get; set; }
	}
	public class SuppressPersonalityEventArgs : EventArgs
	{
		public bool IsSuppressed { get; set; }
	}

	public class MissingConnectionException : ApplicationException
	{
		public MissingConnectionException(string message)
			:base(message)
		{
		}
	}
}
