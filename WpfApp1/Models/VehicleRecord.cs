using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WpfApp1.Models
{
    // DB 저장용 레코드 (Speed/DB 프로젝트 호환 필드 포함)
    public sealed class VehicleRecord
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public DateTime DetectTime { get; set; }

        public int TrackId { get; set; }

        public int ClassId { get; set; }
        public string? ClassName { get; set; }

        public double Confidence { get; set; }

        // 박스(픽셀)
        public int BBoxX { get; set; }
        public int BBoxY { get; set; }
        public int BBoxW { get; set; }
        public int BBoxH { get; set; }

        // Lane
        public int LaneNumber { get; set; }

        // Speed
        public int Speed { get; set; }               // 단위: 프로젝트 기준(예: km/h로 쓰면 됨)
        public double SpeedKmh { get; set; }         // 단위: km/h (있으면 사용)

        // Direction / Violation
        public string? Direction { get; set; }
        public bool IsViolation { get; set; }
        public string? ViolationReason { get; set; }

        // Plate (옵션)
        public string? LicensePlate { get; set; }
    }
}
