```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.87GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3
  Job-HILDPN : .NET 10.0.7 (10.0.7, 10.0.726.21808), X64 RyuJIT x86-64-v3

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                                                   | Mean    | Error   | StdDev  | Completed Work Items | Lock Contentions | Gen0       | Gen1      | Allocated |
|------------------------------------------------------------------------- |--------:|--------:|--------:|---------------------:|-----------------:|-----------:|----------:|----------:|
| &#39;Canonical_Representative_Workflow_Runs_On_Supported_Backends (Azurite)&#39; | 39.25 s | 3.499 s | 0.192 s |           71508.0000 |           7.0000 | 36000.0000 | 4000.0000 | 615.33 MB |
