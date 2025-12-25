using MongoDB.Driver;
using System;
using System.Threading.Tasks;
using WpfApp1.Models;
using WpfApp1.Scripts;

namespace WpfApp1.Services
{
    public class DataService
    {
        private readonly IMongoCollection<VehicleRecord>? _dbCollection;

        public DataService(string connectionString)
        {
            try
            {
                var client = new MongoClient(connectionString);
                var database = client.GetDatabase("TrafficControlDB");
                _dbCollection = database.GetCollection<VehicleRecord>("DetectionLogs");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DB 연결 실패: {ex.Message}");
            }
        }

        // ✅ 뷰모델에서 호출할 때 에러가 나지 않도록 정확한 시그니처로 정의
        public void UpdateRealtimeDb(TrackedObject track, string? modelName)
        {
            if (_dbCollection == null) return;

            Task.Run(async () =>
            {
                try
                {
                    string violation = track.CheckViolation();
                    var filter = Builders<VehicleRecord>.Filter.Eq("TrackId", track.Id);

                    var update = Builders<VehicleRecord>.Update
                        .Set("SystemTime", DateTime.Now)
                        .Set("Speed", Math.Round(track.SpeedInKmh, 1))
                        .Set("ViolationReason", violation)
                        .Set("LaneNumber", track.CurrentLane)
                        .Set("VehicleType", track.LastClassName ?? "Vehicle")
                        .SetOnInsert("FirstDetectedTime", track.FirstDetectedTime)
                        .SetOnInsert("ModelName", modelName ?? "Unknown");

                    await _dbCollection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
                }
                catch { /* DB 예외 처리 */ }
            });
        }
    }
}