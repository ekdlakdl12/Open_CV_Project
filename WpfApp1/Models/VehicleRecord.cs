using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WpfApp1.Models
{
    public class VehicleRecord
    {
        [BsonId]
        public ObjectId Id { get; set; }
        public DateTime DetectTime { get; set; }
        public string VehicleType { get; set; }
        public string Direction { get; set; }
        public int Speed { get; set; }
        public string ViolationReason { get; set; } = "정상";
        public string LicensePlate { get; set; } = "미감지";
    }
}