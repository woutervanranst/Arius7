```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  Job-HILDPN : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                                                   | Mean    | Error   | StdDev  | Completed Work Items | Lock Contentions | Gen0       | Gen1      | Allocated |
|------------------------------------------------------------------------- |--------:|--------:|--------:|---------------------:|-----------------:|-----------:|----------:|----------:|
| &#39;Canonical_Representative_Workflow_Runs_On_Supported_Backends (Azurite)&#39; | 39.14 s | 5.233 s | 0.287 s |           71845.0000 |          11.0000 | 36000.0000 | 7000.0000 | 618.62 MB |
