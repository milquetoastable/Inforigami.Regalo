using System;

namespace Inforigami.Regalo.Interfaces
{
    public abstract class Command : Message, ICommand
    {
        public int TargetVersion { get; set; }
    }
}