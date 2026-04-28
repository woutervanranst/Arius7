```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.4 (25E246) [Darwin 25.4.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-HILDPN : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                                                   | Mean    | Error    | StdDev   | Gen0        | Completed Work Items | Lock Contentions | Gen1       | Gen2       | Allocated |
|------------------------------------------------------------------------- |--------:|---------:|---------:|------------:|---------------------:|-----------------:|-----------:|-----------:|----------:|
| &#39;Canonical_Representative_Workflow_Runs_On_Supported_Backends (Azurite)&#39; | 1.404 m | 0.1645 m | 0.0090 m | 409000.0000 |          459541.0000 |           7.0000 | 73000.0000 | 43000.0000 |   4.77 GB |
