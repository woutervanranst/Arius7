```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.4 (25E246) [Darwin 25.4.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-HILDPN : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                 | Mean    | Error   | StdDev  | Gen0       | Completed Work Items | Lock Contentions | Gen1      | Allocated |
|--------------------------------------- |--------:|--------:|--------:|-----------:|---------------------:|-----------------:|----------:|----------:|
| Archive_Step_V1_Representative_Azurite | 38.66 s | 6.094 s | 0.334 s | 44000.0000 |           30136.0000 |                - | 5000.0000 | 359.31 MB |
