using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WpfApp1.Models
{
    public class VehicleRecord
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public DateTime DetectTime { get; set; }
        public string VehicleType { get; set; } = "";    // ✅ 추가
        public string Direction { get; set; } = "";
        public int Speed { get; set; }
        public string ViolationReason { get; set; } = ""; // ✅ 추가
        public string LicensePlate { get; set; } = "";   // ✅ 추가
    }
}