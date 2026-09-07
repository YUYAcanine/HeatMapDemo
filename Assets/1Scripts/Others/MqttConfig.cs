using System;
using System.IO;
using UnityEngine;

/// <summary>
/// MQTT 接続先を 1 か所で管理するための共通設定。
///
/// Assets/IP/MqttConnection.json の host / port を書き換えるだけで、
/// すべての Publisher / Subscriber 系スクリプトの接続先が切り替わる。
///
/// ファイルが存在しない・壊れている場合は、各コンポーネントの
/// Inspector に設定された値（fallback）をそのまま使う。
/// </summary>
public static class MqttConfig
{
    [Serializable]
    private class ConnectionData
    {
        public string host;
        public int port = 1883;
    }

    // Assets からの相対パス。Application.dataPath が Assets を指す（エディタ実行時）。
    private const string RelativePath = "IP/MqttConnection.json";

    private static ConnectionData cached;
    private static bool loaded;

    public static string FilePath => Path.Combine(Application.dataPath, RelativePath);

    private static ConnectionData Load()
    {
        if (loaded)
            return cached;

        loaded = true;
        cached = null;

        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"MqttConfig: {path} が見つかりません。各コンポーネントの Inspector 値を使用します。");
                return null;
            }

            string json = File.ReadAllText(path);
            ConnectionData data = JsonUtility.FromJson<ConnectionData>(json);
            if (data == null || string.IsNullOrWhiteSpace(data.host))
            {
                Debug.LogWarning($"MqttConfig: {path} の内容が不正です。Inspector 値を使用します。");
                return null;
            }

            cached = data;
            Debug.Log($"MqttConfig: {data.host}:{data.port} を {path} から読み込みました。");
            return cached;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"MqttConfig: 読み込み失敗 {ex.GetType().Name}: {ex.Message}. Inspector 値を使用します。");
            return null;
        }
    }

    /// <summary>ファイルに有効な host があればそれを、無ければ fallback を返す。</summary>
    public static string ResolveHost(string fallback)
    {
        ConnectionData data = Load();
        return data != null && !string.IsNullOrWhiteSpace(data.host) ? data.host : fallback;
    }

    /// <summary>ファイルに有効な port があればそれを、無ければ fallback を返す。</summary>
    public static int ResolvePort(int fallback)
    {
        ConnectionData data = Load();
        return data != null && data.port > 0 ? data.port : fallback;
    }

    /// <summary>再生中にファイルを読み直したい場合に呼ぶ（任意）。</summary>
    public static void Reload()
    {
        loaded = false;
        cached = null;
    }
}
