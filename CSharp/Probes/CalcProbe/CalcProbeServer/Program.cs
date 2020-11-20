﻿using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using CalcProbeServer.Storage;
using Grpc.Core;
using Google.Protobuf;
using LinNet;
using Calc = Generated.Calc;
using FieldDef = Optima.Domain.DatasetDefinition.FieldDef;
using Req = Generated.Req;
using ReqWithLineage = Generated.ReqWithLineage;
using Resp = Generated.Resp;
using RespWithLineage = Generated.RespWithLineage;

namespace CalcProbeServer
{
    public class GeneratedCalcBase : Calc.CalcBase
    {
        private readonly ImmutableDictionary<string, ImmutableArray<Optima.Domain.DatasetDefinition.FieldDef>> _fieldMapping;
        public CalculatorId CalculatorId { get; }

        public GeneratedCalcBase(CalculatorId calculatorId, ImmutableDictionary<string, ImmutableArray<Optima.Domain.DatasetDefinition.FieldDef>> fieldMapping)
        {
            _fieldMapping = fieldMapping;
            CalculatorId = calculatorId;
        }
        
        public override Task Run(IAsyncStreamReader<Req> requestStream, IServerStreamWriter<Resp> responseStream, ServerCallContext context)
        {
            return base.Run(requestStream, responseStream, context);
        }

        public override async Task RunWithLineage(IAsyncStreamReader<ReqWithLineage> requestStream, IServerStreamWriter<RespWithLineage> responseStream, ServerCallContext context)
        {
            var rowIndex = 0;
            var reader = new Reader(requestStream);
            var writer = new Writer(responseStream, resp => 
                new RespWithLineage
                {
                    RowResponse = _fieldMapping?.Count > 0 
                        ? new RespWithLineage.Types.RowResponse
                            {
                                Row = { resp }, 
                                RowLineage =
                                {
                                    new RowLineage
                                    {
                                        Lineage = { _fieldMapping.Keys.Select(p => (p, new RowLineage.Types.RowFieldLineage{ RowId = rowIndex++, CalculatorId = CalculatorId, Parents = { _fieldMapping[p] }})).
                                            ToDictionary(kv => kv.p, kv => kv.Item2) }
                                    }
                                } // TODO: Populate Parents
                            }
                        : new RespWithLineage.Types.RowResponse
                            {
                                Row = { resp } // TODO: Populate Parents
                            }
                });
            
            await Run(reader, writer, context);

            if (reader.ParentLineage != null)
            {
                await responseStream.WriteAsync(new RespWithLineage {DatasetLineage = reader.ParentLineage}); // TODO: Add CalcLineage and decorate
            }
        }
        
        // public static ImmutableArray<string> FieldNames = typeof(Resp).GetProperties().Select(p => p.Name).ToImmutableArray();

        public override Task<Req> Echo(Req request, ServerCallContext context) => Task.FromResult(request);

        class Reader : IAsyncStreamReader<Req>
        {
            private readonly IAsyncStreamReader<ReqWithLineage> _parentReader;

            public Reader(IAsyncStreamReader<ReqWithLineage> parentReader)
            {
                _parentReader = parentReader;
            }
            
            public async Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                var ret = await _parentReader.MoveNext();

                while (ret && _parentReader.Current.CaseCase == ReqWithLineage.CaseOneofCase.DatasetLineage) // Skipping Lineage information while proxying
                {
                    ParentLineage = _parentReader.Current.DatasetLineage;
                    ret = await _parentReader.MoveNext();
                }

                return ret;
            }

            public Req Current => _parentReader.Current?.Request;
            
            public DatasetLineage ParentLineage { get; private set; }
        }

        class Writer : IServerStreamWriter<Resp>
        {
            private readonly IServerStreamWriter<RespWithLineage> _parentWriter;
            private readonly Func<Resp, RespWithLineage> _rowLineageApplier;

            public Writer(IServerStreamWriter<RespWithLineage> parentWriter, Func<Resp, RespWithLineage> rowLineageApplier)
            {
                _parentWriter = parentWriter;
                _rowLineageApplier = rowLineageApplier;
            }
            
            public async Task WriteAsync(Resp message) => await _parentWriter.WriteAsync(_rowLineageApplier(message));

            public WriteOptions WriteOptions
            {
                get => _parentWriter.WriteOptions;
                set => _parentWriter.WriteOptions = value;
            }
        }
    }

    public class GeneratedCalcProxy : GeneratedCalcBase
    {
        private readonly Calc.CalcClient _calcClient;
        private readonly RocksDbWrapper _rocks;
        private readonly CallOptions _options;

        public GeneratedCalcProxy(CalculatorId calculatorId, ImmutableDictionary<string, ImmutableArray<Optima.Domain.DatasetDefinition.FieldDef>> fieldMapping, Calc.CalcClient calcClient, CallOptions options = default, string rocksRootFolder = null) : base(calculatorId, fieldMapping)
        {
            _calcClient = calcClient;
            _options = options;
            _rocks = rocksRootFolder != null ? new RocksDbWrapper(rocksRootFolder) : null;
        }

        public override async Task Run(IAsyncStreamReader<Req> requestStream, IServerStreamWriter<Resp> responseStream, ServerCallContext context)
        {
            var proxyRun = _calcClient.Run(_options);
            var rocksChannel = System.Threading.Channels.Channel.CreateUnbounded<Resp>();
            var runUid = Guid.NewGuid().ToString("N");

            var readTask = Task.Run(async () =>
                {
                    while (await requestStream.MoveNext()) 
                        await proxyRun.RequestStream.WriteAsync(requestStream.Current);

                    await proxyRun.RequestStream.CompleteAsync();
                });
            
            var writeTask = Task.Run(async () =>
            {
                while (await proxyRun.ResponseStream.MoveNext())
                {
                    if (_rocks != null)
                    {
                        await rocksChannel.Writer.WriteAsync(proxyRun.ResponseStream.Current);
                    }
                    await responseStream.WriteAsync(proxyRun.ResponseStream.Current);
                }
            });
            
            var dbTask = _rocks == null 
                ? Task.CompletedTask 
                : Task.Run(() => _rocks.Write(rocksChannel.Reader.ReadAllAsync().ToEnumerable().Select(r => r.ToByteArray()), runUid));

            await readTask;
            await writeTask;
            await dbTask;
        }
        
        public override Task<Req> Echo(Req request, ServerCallContext context) => _calcClient.EchoAsync(request, _options).ResponseAsync;
    }
    
    public class ExampleCalcImpl : Calc.CalcBase
    {
        public override async Task Run(IAsyncStreamReader<Req> requestStream, IServerStreamWriter<Resp> responseStream, ServerCallContext context)
        {
            while (await requestStream.MoveNext()) 
                await responseStream.WriteAsync(new Resp { Field1 = $"C# Calc F1: {requestStream.Current.Field1}", Field2 = 1_000_000 + requestStream.Current.Field2 });
        }

        public override Task<Req> Echo(Req request, ServerCallContext context) => Task.FromResult(request);
    }

    class Program
    {
        const int PortBase = 5050;
        static TaskCompletionSource<int> shutdownFlag = new TaskCompletionSource<int>();
        
        static void Main(string[] args)
        {
            ImmutableDictionary<string, ImmutableArray<FieldDef>> fieldMapping = default; // TODO: Read from arguments
            // ImmutableDictionary<string, ImmutableArray<FieldDef>> fieldMapping = new []
            // {
            //     ("Field1", new[] {new Optima.Domain.DatasetDefinition.FieldDef { Name = "Field2" }}.ToImmutableArray()), 
            //     ("Field2", new[] {new Optima.Domain.DatasetDefinition.FieldDef { Name = "Field2" }}.ToImmutableArray() )
            // }.ToImmutableDictionary(kv => kv.Item1, kv => kv.Item2);

            // ReSharper disable ExpressionIsAlwaysNull
            var server = StartServer(PortBase + 1, new ExampleCalcImpl());
            var proxySharp = StartServer(PortBase + 2, 
                new GeneratedCalcProxy(new CalculatorId { Uid = Guid.NewGuid().ToString("N")}, 
                    fieldMapping,
                    new Calc.CalcClient(new Channel("localhost", PortBase + 1, ChannelCredentials.Insecure))));
            var proxyPython = StartServer(PortBase + 3, new GeneratedCalcProxy(new CalculatorId { Uid = Guid.NewGuid().ToString("N")}, 
                fieldMapping,
                new Calc.CalcClient(new Channel("localhost", PortBase + 5, ChannelCredentials.Insecure))));
            // ReSharper restore ExpressionIsAlwaysNull

            Console.WriteLine("Server and proxies started");
            Console.WriteLine("Press any key to stop ...");
            Console.ReadKey();

            shutdownFlag.SetResult(0);
            
            Task.WaitAll(server, proxySharp, proxyPython);
        }

        static Task StartServer(int port, Calc.CalcBase handler) => Task.Run(async () =>
        {
            var server = new Server
            {
                Services = {Calc.BindService(handler)},
                Ports = {new ServerPort("localhost", port, ServerCredentials.Insecure)}
            };
            server.Start();

            Console.WriteLine("ExampleCalc server listening on port " + port);

            await shutdownFlag.Task;
            await server.ShutdownAsync();
        });
    }
}