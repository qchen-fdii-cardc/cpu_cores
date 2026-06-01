# CPU_Cores


用dotnet（F#）实现一个比较P核和E核的并行计算性能的程序。

目的：比较Intel CPU的P核（Performance Core）和E核（Efficiency Core）在并行计算任务中的性能差异。

方法：实现一个并行的计算程序，利用Monte Carlo法估算圆周率（π）。程序将分别在P核和E核上并行，再用P核/E核混合并行，单个P核和单个E核上运行相同的计算任务。通过比较不同核心配置下的计算时间，评估P核和E核的性能差异。

在考核计算之前，利用系统调用把CPU的速度调成最高，以确保测试的公平性。


```shell
E:\sources\cpu_cores\bin\Release\net11.0\win-x64\publish> .\CpuCores.exe
CPU P-core / E-core Monte Carlo Benchmark
Samples per benchmark: 50000000
Repeats per case: 20

[Power] CPU policy configured for benchmarking (high performance).
[CPU] EfficiencyClass mapping: P=1, E=0
[CPU] P one-thread set (Np): [|0; 2; 4; 6; 8; 10; 12; 14|]
[CPU] P all-logical set (2*Np): [|0; 1; 2; 3; 4; 5; 6; 7; 8; 9; 10; 11; 12; 13; 14; 15|]
[CPU] E logical set (Ne): [|16; 17; 18; 19|]
[CPU] Core count: Np=8, Ne=4
[CPU] Check: Np + Ne = 12
[CPU] Check: 2*Np + Ne = 20
[CPU] Mixed logical processors: [|0; 1; 2; 3; 4; 5; 6; 7; 8; 9; 10; 11; 12; 13; 14; 15; 16; 17; 18; 19|]

Results
-------
Case                   Workers   n         Pi (mean +/- sd)   Time s (mean +/- sd)         Throughput M/s         Per-thread M/s
------------------------------------------------------------------------------------------------------------------------------------
P cores Np threads           8  20  3.14165606 +/- 0.00021473    0.2271 +/- 0.0522      235.27 +/- 69.55        29.41 +/- 8.69
P cores 2*Np threads        16  20  3.14165610 +/- 0.00024107    0.1700 +/- 0.0067      294.50 +/- 10.97        18.41 +/- 0.69
E cores Ne threads           4  20  3.14170877 +/- 0.00016880    0.8244 +/- 0.2117       64.92 +/- 17.86        16.23 +/- 4.46
P/E mixed (2*Np+Ne)         20  20  3.14163832 +/- 0.00028934    0.2775 +/- 0.1022      199.94 +/- 59.23        10.00 +/- 2.96
Single P core                1  20  3.14167406 +/- 0.00023585    1.9845 +/- 0.0014       25.20 +/- 0.02         25.20 +/- 0.02
Single E core                1  20  3.14167406 +/- 0.00023585    2.3142 +/- 0.1158       21.66 +/- 1.05         21.66 +/- 1.05
```