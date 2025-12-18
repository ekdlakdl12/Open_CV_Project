using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

//DB에 저장할 차량 기록 모델
namespace WpfApp1.Models
{
    public class VehicleRecord
    {
        [BsonId]
        // MongoDB에서 자동으로 ID를 생성하도록 설정
        public ObjectId Id { get; set; }

        // 한국 시간으로 저장하기 위해 DateTime 속성 유지
        public DateTime DetectTime { get; set; }

        public string VehicleType { get; set; }

        public string Direction { get; set; }

        public int Speed { get; set; }
    }
}