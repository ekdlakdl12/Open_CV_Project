namespace WpfApp1.Models
{
    public class DetectedObject : ObservableObject
    {
        private string _objectType;
        private int _count;

        public string ObjectType
        {
            get => _objectType;
            set => SetProperty(ref _objectType, value);
        }

        public int Count
        {
            get => _count;
            set => SetProperty(ref _count, value);
        }
    }
}