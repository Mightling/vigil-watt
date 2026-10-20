using Xunit;

// Log 是全局静态状态（MinLevel / 重定向目录 / 写入句柄），测试并行时多个类会互相踩
// （LogTests 与 HistoryStoreTests 都会 RedirectTo）。关掉测试集内的并行，保证日志相关断言稳定。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
