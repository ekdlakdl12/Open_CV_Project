using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace WpfApp1.Models
{
    public class VehicleRecord
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public DateTime SystemTime { get; set; }      // DB에 저장되는 실제 서버/로컬 시간
        public string FirstDetectedTime { get; set; } = ""; // 객체가 처음 인식된 시간 (HH:mm:ss)
        public string VehicleType { get; set; } = "";  // CAR, BUS, TRUCK 등
        public string ModelName { get; set; } = "";    // 구체적 모델 명칭
        public double Speed { get; set; }              // 계산된 속도 (km/h)
        public int LaneNumber { get; set; }            // 현재 주행 중인 차선 번호
        public string Direction { get; set; } = "";    // 진행 방향 (L, F, R)
        public string ViolationReason { get; set; } = ""; // 과속 여부 (100km/h 초과 시 위반)
    }
}