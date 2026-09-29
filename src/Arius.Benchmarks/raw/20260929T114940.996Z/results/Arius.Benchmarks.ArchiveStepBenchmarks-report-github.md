```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-HILDPN : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                 | Mean    | Error   | StdDev   | Gen0       | Completed Work Items | Lock Contentions | Gen1      | Gen2      | Allocated |
|--------------------------------------- |--------:|--------:|---------:|-----------:|---------------------:|-----------------:|----------:|----------:|----------:|
| Archive_Step_V1_Representative_Azurite | 3.445 s | 7.194 s | 0.3943 s | 29000.0000 |           28159.0000 |         491.0000 | 9000.0000 | 1000.0000 |  392.1 MB |
