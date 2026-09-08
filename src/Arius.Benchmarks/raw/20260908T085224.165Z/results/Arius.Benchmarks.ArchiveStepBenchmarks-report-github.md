```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-HILDPN : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                 | Mean    | Error   | StdDev   | Gen0       | Completed Work Items | Lock Contentions | Gen1       | Gen2      | Allocated |
|--------------------------------------- |--------:|--------:|---------:|-----------:|---------------------:|-----------------:|-----------:|----------:|----------:|
| Archive_Step_V1_Representative_Azurite | 3.808 s | 3.631 s | 0.1990 s | 33000.0000 |           28623.0000 |         429.0000 | 11000.0000 | 2000.0000 | 407.74 MB |
