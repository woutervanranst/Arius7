```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.4 (25E246) [Darwin 25.4.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-HILDPN : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                 | Mean     | Error    | StdDev   | Gen0      | Completed Work Items | Lock Contentions | Gen1      | Allocated |
|--------------------------------------- |---------:|---------:|---------:|----------:|---------------------:|-----------------:|----------:|----------:|
| Archive_Step_V1_Representative_Azurite | 197.7 ms | 770.0 ms | 42.21 ms | 6000.0000 |           12856.0000 |           4.0000 | 2000.0000 |  48.52 MB |
