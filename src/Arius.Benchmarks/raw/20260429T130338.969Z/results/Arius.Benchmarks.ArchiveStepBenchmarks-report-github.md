```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.4 (25E246) [Darwin 25.4.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-HILDPN : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=3  LaunchCount=1  
UnrollFactor=1  WarmupCount=0  

```
| Method                                  | Mean     | Error     | StdDev   | Gen0      | Completed Work Items | Lock Contentions | Allocated |
|---------------------------------------- |---------:|----------:|---------:|----------:|---------------------:|-----------------:|----------:|
| Archive_Step_V1_Representative_InMemory | 33.58 ms | 135.01 ms | 7.400 ms | 1000.0000 |            9227.0000 |                - |   9.69 MB |
