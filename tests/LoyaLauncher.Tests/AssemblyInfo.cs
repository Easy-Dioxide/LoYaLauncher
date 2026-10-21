using Xunit;

// LoyaPaths.Root 是进程级静态状态，测试通过重定向它隔离数据。
// 并行测试会互相覆盖该静态值，因此禁用并行，全部串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
