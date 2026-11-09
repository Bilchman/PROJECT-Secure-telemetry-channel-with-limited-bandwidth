namespace Core
{
    public class Channel : IChannel
    {
        private readonly double _lossProbability;
        private readonly double _burstLengthB;
        private readonly int _delayMs;
        private readonly int _bandwidthBps;
        private readonly double _transmissionBudgetS;
        
        private readonly Random _rng;
        private readonly double _pGb;
        private int _inBurst = 0;
        private double _clock = 0.0;
        
        private int _sent = 0;
        private int _lost = 0;
        private int _overBudget = 0;

        public Channel(
            double lossProbability, 
            double burstLengthB, 
            int delayMs, 
            int bandwidthBps, 
            double transmissionBudgetS, 
            int seed)
        {
            _lossProbability = lossProbability;
            _burstLengthB = burstLengthB;
            _delayMs = delayMs;
            _bandwidthBps = bandwidthBps;
            _transmissionBudgetS = transmissionBudgetS;

            _rng = new Random(seed);

            if (_lossProbability > 0)
            {
                _pGb = _lossProbability / 
                       (_lossProbability + _burstLengthB * (1.0 - _lossProbability));
            }
            else
            {
                _pGb = 0.0;
            }
        }

        public ChannelResult Send(ReadOnlySpan<byte> packetBytes)
        {
            int bits = packetBytes.Length * 8;
            double transmissionTime = (double)bits / _bandwidthBps;

            if (_clock + transmissionTime > _transmissionBudgetS)
            {
                _overBudget++;
                return new ChannelResult(Delivered: false, ArrivalTimeSec: -1.0, DroppedByBudget: true);
            }

            _clock += transmissionTime;
            _sent++;

            bool lost;
            if (_inBurst > 0)
            {
                _inBurst--;
                lost = true;
            }
            else if (_lossProbability > 0 && _rng.NextDouble() < _pGb)
            {
                int length = 1;
                if (_burstLengthB > 1.0)
                {
                    while (_rng.NextDouble() > (1.0 / _burstLengthB))
                    {
                        length++;
                    }
                }
                _inBurst = length - 1;
                lost = true;
            }
            else
            {
                lost = false;
            }

            if (lost)
            {
                _lost++;
                return new ChannelResult(Delivered: false, ArrivalTimeSec: -1.0, DroppedByBudget: false);
            }

            double arrivalTime = _clock + (_delayMs / 1000.0);
            return new ChannelResult(Delivered: true, ArrivalTimeSec: arrivalTime, DroppedByBudget: false);
        }

        public Dictionary<string, object> GetStats()
        {
            int totalAttempted = _sent + _overBudget;
            double lossRatio = _sent == 0 ? 0.0 : (double)_lost / _sent;
            double undeliveredRatio = totalAttempted == 0 ? 0.0 : (double)(_lost + _overBudget) / totalAttempted;

            return new Dictionary<string, object>
            {
                ["надіслано"] = _sent,
                ["втрачено"] = _lost,
                ["не_вміщено_в_бюджет"] = _overBudget,
                ["частка_втрат"] = lossRatio,
                ["частка_недоставлених"] = undeliveredRatio,
                ["час_каналу_с"] = _clock,
                ["бюджет_с"] = _transmissionBudgetS
            };
        }
    }
}