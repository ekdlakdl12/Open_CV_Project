using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WpfApp1.Models
{
    public class VehicleRecord
    {
        [BsonId]
        [BsonIgnoreIfDefault]
        public ObjectId Id { get; set; }

        // 이 부분이 누락되어 오류가 발생한 것입니다.
        [BsonElement("TrackId")]
        public int TrackId { get; set; }

        [BsonElement("FirstDetectedTime")]
        public string FirstDetectedTime { get; set; } = string.Empty;

        [BsonElement("SystemTime")]
        public DateTime SystemTime { get; set; }

        [BsonElement("LaneNumber")]
        public int LaneNumber { get; set; }

        [BsonElement("VehicleType")]
        public string VehicleType { get; set; } = string.Empty;

        [BsonElement("ModelName")]
        public string ModelName { get; set; } = string.Empty;

        [BsonElement("Speed")]
        public double Speed { get; set; }

        [BsonElement("ViolationReason")]
        public string ViolationReason { get; set; } = string.Empty;
    }
}