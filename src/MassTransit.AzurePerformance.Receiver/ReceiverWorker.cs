using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using Magnum.Extensions;
using MassTransit.AzurePerformance.Messages;
using MassTransit.Pipeline.Inspectors;
using Microsoft.WindowsAzure.Diagnostics;
using Microsoft.WindowsAzure.ServiceRuntime;
using MassTransit.Transports.AzureServiceBus.Configuration;
using log4net.Config;

namespace MassTransit.AzurePerformance.Receiver
{
	public class ReceiverWorker : RoleEntryPoint
	{
		volatile bool _isStopping;

		public override void Run()
		{
			//BasicConfigurator.Configure();
			// This is a sample worker implementation. Replace with your logic.
			Trace.WriteLine("Run Receiver", "Information");
			RoleEnvironment.Stopping += (sender, args) => _isStopping = true;

			ConfigureDiagnostics();

			var stopping = new AutoResetEvent(false);

			const long rampUp = 50;
			const long sampleSize = 300;

			long failures = 0;
			long received = 0;
			var watch = new Stopwatch();
			var datapoints = new LinkedList<DataPoint>();

			using (var sb = ServiceBusFactory.New(sbc =>
				{
					sbc.ReceiveFromComponents(AccountDetails.IssuerName,
						AccountDetails.Key, AccountDetails.Namespace,
						"receiver");

					sbc.SetPurgeOnStartup(true);
					
					sbc.UseAzureServiceBusRouting();
				}))
			{
				UnsubscribeAction unsubscribeMe = null;
				//unsubscribeMe = sb.SubscribeHandler<IConsumeContext<ZoomZoom>>(consumeContext =>
				unsubscribeMe = sb.SubscribeHandler<ZoomZoom>(payment =>
					{
						//var payment = consumeContext.Message;
						
						long currentReceived;
						if ((currentReceived = Interlocked.Increment(ref received)) == rampUp)
							watch.Start();
						else if (currentReceived < rampUp) return;

						var localFailures = new long?();
						if (Math.Abs(payment.Amount - 1024m) > 0.0001m)
						{
							localFailures = Interlocked.Increment(ref failures);
						}

						if (currentReceived + rampUp == sampleSize || _isStopping)
						{
							unsubscribeMe();
							watch.Stop();
							stopping.Set();
						}

						if (currentReceived % 100 == 0)
						{
							var point = new DataPoint
								{
									Received = currentReceived,
									Ticks = watch.ElapsedTicks,
									Failures = localFailures ?? failures,
									SampleMessage = payment,
									/* assume all prev 100 msgs same size */
									//Size = consumeContext.BaseContext.BodyStream.Length * 100 
								};
							lock (datapoints) datapoints.AddLast(point);
							Trace.WriteLine(string.Format("Logging {0}", point), "Trace");
						}
					});

				stopping.WaitOne(1.Seconds());

				PipelineViewer.Trace(sb.InboundPipeline);

				stopping.WaitOne();

				sb.GetEndpoint(new Uri(string.Format("azure-sb://owner:{0}@{1}/sender", AccountDetails.Key, AccountDetails.Namespace)))
					.Send<ZoomDone>(new{});
			}

			Trace.WriteLine(
string.Format(@"
Performance Test Done
=====================

Total messages received:	{0}
Time taken:					{1}

of which:
	Corrupt messages count:	{2}
	Valid messages count:	{3}

metrics:
	Message per second:		{4}
	Total bytes transferred:{5}
	All samples' data equal:{6}
",
 received-rampUp, watch.Elapsed, failures,
 sampleSize-failures,
 1000d * sampleSize / (double)watch.ElapsedMilliseconds,
 datapoints.Sum(dp => dp.Size),
 datapoints.Select(x => x.SampleMessage).All(x => x.Payload.Equals(TestData.PayloadMessage, StringComparison.InvariantCulture))));

			Trace.WriteLine("Idling... aka. softar.", "Information");
			
			while (true)
				Thread.Sleep(10000);
		}

		class DataPoint
		{
			public long Received { get; set; }
			public long Failures { get; set; }
			public long Ticks { get; set; }
			/// <summary>Size since last data point</summary>
			public long Size { get; set; }

			public ZoomZoom SampleMessage { get; set; }

			public override string ToString() {
				return "DP={{Rec:{0},Fail:{1},Ticks:{2}}}".FormatWith(Received, Failures, Ticks);
			}
		}

		public override bool OnStart()
		{
			ServicePointManager.DefaultConnectionLimit = 12;
			
			ConfigureDiagnostics();

			return base.OnStart();
		}

		void ConfigureDiagnostics()
		{
			var everySecond = 1.Seconds();
			var dmc = DiagnosticMonitor.GetDefaultInitialConfiguration();
			dmc.Logs.ScheduledTransferPeriod = everySecond;
			dmc.Logs.ScheduledTransferLogLevelFilter = LogLevel.Verbose;
			DiagnosticMonitor.Start("Microsoft.WindowsAzure.Plugins.Diagnostics.ConnectionString", dmc);
		}
	}
}