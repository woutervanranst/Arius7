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
| Archive_Step_V1_Representative_Azurite | 34.24 s | 5.323 s | 0.292 s | 57000.0000 |           39603.0000 |           6.0000 | 6000.0000 | 455.67 MB |
