using System.Text.Json;
namespace UnityBridgeDesk.Infrastructure.SpeedBench;
public static class SceneOracle
{
    public static void Validate(JsonElement objects, int count)
    {
        if (objects.ValueKind != JsonValueKind.Array || objects.GetArrayLength() != count) throw new SpeedMeasurementException("response-mismatch", "최종 씬의 오브젝트 수가 다릅니다.");
        var seen = new HashSet<int>();
        foreach (var item in objects.EnumerateArray())
        {
            string name = item.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("DeskFixed_", StringComparison.Ordinal) || !int.TryParse(name[10..], out int index) || index < 0 || index >= count ||
                name != "DeskFixed_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) || !seen.Add(index))
                throw new SpeedMeasurementException("response-mismatch", "최종 씬의 ID·중복 검사 실패.");
            double[] expected = [index % 100, index / 100, index % 7];
            string[] fields = ["x", "y", "z"];
            double distance = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double actual = item.GetProperty(fields[axis]).GetDouble();
                if (!double.IsFinite(actual)) throw new SpeedMeasurementException("response-mismatch", "유한하지 않은 위치입니다.");
                distance += Math.Pow(actual - expected[axis], 2);
            }
            if (distance >= 1e-10) throw new SpeedMeasurementException("response-mismatch", "최종 씬 위치 검사 실패 (거리 허용 0.00001 미만).");
        }
    }
}
