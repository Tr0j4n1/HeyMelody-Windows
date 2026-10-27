using System;
using System.Collections.Generic;

namespace MelodyDesk
{
    // Keeps the user's noise mode across the charging case. The buds pick their own mode as buds go in or come out
    // (Off with both in the case, Adaptive with one in, often Transparency when worn again), so only what the user
    // picks, or what the buds report while both are worn, counts as the choice; it is put back when both are worn again.
    sealed class NoiseMemory
    {
        static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(4); // the buds report their own pick within ~1 s of being worn
        const int MaxRestores = 3;

        readonly Action<int> apply;
        readonly Func<DateTime> clock;
        readonly Dictionary<int, EarStatus> ears = new Dictionary<int, EarStatus>();
        DateTime restoreUntil;
        int restores;

        public int Chosen = -1;   // the user's mode
        public int Reported = -1; // what the buds last said

        public NoiseMemory(Action<int> apply, Func<DateTime> clock)
        {
            this.apply = apply;
            this.clock = clock;
        }

        public bool EarsKnown { get { return ears.ContainsKey(1) && ears.ContainsKey(2); } }
        public bool BothWorn { get { return EarsKnown && ears[1].InEar && ears[2].InEar; } }
        public bool NoneWorn { get { return EarsKnown && !ears[1].InEar && !ears[2].InEar; } }
        public int Shown { get { return Chosen >= 0 ? Chosen : Reported; } }

        public void OnReport(int bit)
        {
            Reported = bit;
            if (!BothWorn) return; // a bud is out or in the case: the buds' own pick
            if (clock() < restoreUntil) { Restore(); return; } // the mode the buds came out of the case in
            Chosen = bit; // changed by the user, in the app or with a long-press on the buds
        }

        public void OnEars(IEnumerable<EarStatus> statuses)
        {
            bool wasBothWorn = BothWorn;
            foreach (var s in statuses) ears[s.Device] = s;
            if (!BothWorn || wasBothWorn || Chosen < 0) return;
            restoreUntil = clock() + SettleTime;
            restores = 0;
            Restore();
        }

        // False when no bud is worn: HeyMelody refuses noise changes then too.
        public bool Pick(int bit)
        {
            if (NoneWorn) return false;
            Chosen = bit;
            apply(bit);
            return true;
        }

        // Link dropped: wear state and the buds' report are unknown until they talk again; the choice is kept.
        public void Disconnected()
        {
            ears.Clear();
            Reported = -1;
        }

        public void Forget()
        {
            Disconnected();
            Chosen = -1;
        }

        void Restore()
        {
            if (Chosen < 0 || Reported == Chosen || restores >= MaxRestores) return;
            restores++;
            apply(Chosen);
        }
    }
}
