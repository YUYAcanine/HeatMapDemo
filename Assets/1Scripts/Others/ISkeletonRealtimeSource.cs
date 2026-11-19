using System.Collections.Generic;

// SkeletonRealtimePublisher / SkeletonRealtimePublisher_FULL が骨格を集めるキネクト1台分の入力。
//   - SkeletonRealtimeMulti … 1PhilipDemo/MultiPosition のキネクト(メインスレッドで撮影)
//   - HomeSkeletonLive      … 4HomeExperiment/6SkeletonPublish のキネクト(HomeKinect と一緒に付ける, 別スレッドで撮影)
// LatestBodies の関節座標は部屋(ワールド)座標。
public interface ISkeletonRealtimeSource
{
    IReadOnlyList<SkeletonRealtimeMulti.BodyData> LatestBodies { get; }
    int DeviceIndex { get; }
}
