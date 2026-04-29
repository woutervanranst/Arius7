```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  Job-HILDPN : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                                                   | Mean    | Error   | StdDev  | Completed Work Items | Lock Contentions | Gen0       | Gen1      | Gen2      | Allocated |
|------------------------------------------------------------------------- |--------:|--------:|--------:|---------------------:|-----------------:|-----------:|----------:|----------:|----------:|
| &#39;Canonical_Representative_Workflow_Runs_On_Supported_Backends (Azurite)&#39; | 38.53 s | 4.945 s | 0.271 s |           71761.0000 |           5.0000 | 37000.0000 | 5000.0000 | 1000.0000 | 613.79 MB |
