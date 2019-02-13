using System;
using System.Collections.Generic;
using System.Linq;

namespace DuetAPI.Machine
{
    public class Model : JsonObject, ICloneable
    {
        public Electronics.Model Electronics { get; set; } = new Electronics.Model();
        public List<Fans.Fan> Fans { get; set; } = new List<Fans.Fan>();
        public Heat.Model Heat { get; set; } = new Heat.Model();
        public Job.Model Job { get; set; } = new Job.Model();
        public MessageBox.Model MessageBox { get; set; } = new MessageBox.Model();
        public Move.Model Move { get; set; } = new Move.Model();
        public Network.Model Network { get; set; } = new Network.Model();
        public List<Message> Output { get; set; } = new List<Message>();
        public Scanner.Model Scanner { get; set; } = new Scanner.Model();
        public Sensors.Model Sensors { get; set; } = new Sensors.Model();
        public List<Spindles.Spindle> Spindles { get; set; } = new List<Spindles.Spindle>();
        public State.Model State { get; set; } = new State.Model();
        public List<Storages.Storage> Storages { get; set; } = new List<Storages.Storage>();
        public List<Tools.Tool> Tools { get; set; } = new List<Tools.Tool>();

        public object Clone()
        {
            return new Model
            {
                Electronics = (Electronics.Model)Electronics.Clone(),
                Fans = Fans.Select(fan => (Fans.Fan)fan.Clone()).ToList(),
                Heat = (Heat.Model)Heat.Clone(),
                Job = (Job.Model)Job.Clone(),
                MessageBox = (MessageBox.Model)MessageBox.Clone(),
                Move = (Move.Model)Move.Clone(),
                Network = (Network.Model)Network.Clone(),
                Output = Output.Select(item => (Message)item.Clone()).ToList(),
                Scanner = (Scanner.Model)Scanner.Clone(),
                Sensors = (Sensors.Model)Sensors.Clone(),
                Spindles = Spindles.Select(spindle => (Spindles.Spindle)spindle.Clone()).ToList(),
                State = (State.Model)State.Clone(),
                Storages = Storages.Select(storage => (Storages.Storage)storage.Clone()).ToList(),
                Tools = Tools.Select(tool => (Tools.Tool)tool.Clone()).ToList()
            };
        }
    }
}