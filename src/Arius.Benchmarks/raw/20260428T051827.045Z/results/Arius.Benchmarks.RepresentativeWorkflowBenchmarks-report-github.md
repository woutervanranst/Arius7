```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Platinum 8370C CPU 2.80GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v4
  Job-HILDPN : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v4

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                                                   | Mean    | Error   | StdDev  | Completed Work Items | Lock Contentions | Gen0       | Gen1       | Gen2       | Allocated |
|------------------------------------------------------------------------- |--------:|--------:|--------:|---------------------:|-----------------:|-----------:|-----------:|-----------:|----------:|
| &#39;Canonical_Representative_Workflow_Runs_On_Supported_Backends (Azurite)&#39; | 40.02 s | 6.464 s | 0.354 s |           72548.0000 |          65.0000 | 47000.0000 | 17000.0000 | 12000.0000 |   1.08 GB |
