using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

// 各示例消费同一 SDK 阶段；文案不改变核心准备、权限或允许发送的条件。
internal static class ExampleInitializationProgress
{
    internal static string Format(SessionInitializationProgress progress)
    {
        var text = progress.Stage switch
        {
            SessionInitializationStage.Connecting => "正在创建或恢复会话",
            SessionInitializationStage.PreparingPersistence => "正在准备持久化来源",
            SessionInitializationStage.SessionConfirmed => "会话已确认，保留原会话标识",
            SessionInitializationStage.PreparingRequiredServices => "正在准备必需的端侧供材",
            SessionInitializationStage.InitializingCapabilities => "正在声明平台能力",
            SessionInitializationStage.PreparingObservation => "正在接入呈现与事件观察",
            SessionInitializationStage.Ready => "基础会话接入完成；操作仍依服务端实际状态",
            SessionInitializationStage.Canceled => "本次连接等待已取消；已受理事实保留",
            _ => "会话接入未完成；请保留原会话或请求键核对"
        };
        return text + "（" + progress.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " 秒）";
    }
}
