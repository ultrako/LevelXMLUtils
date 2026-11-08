using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// Chip8Machine's compiler: one node's instruction as a trigger program
public partial class Chip8Machine
{
	void ChainSteps(Steps steps)
	{
		// The first relay runs the rest in order
		for (int i = 1; i < steps.Relays.Count; i++) { steps.Relays[0].Output.Connect(steps.Relays[i].Input); }
	}

	// One instruction: its Next relay disables itself and runs the instruction's steps
	void Compile(Node node)
	{
		int a = node.Addr, op = Op(a), xr = (op >> 8) & 0xF, yr = (op >> 4) & 0xF, n = op & 0xF, nn = op & 0xFF, nnn = op & 0xFFF;
		InstantToggleChain vx = V[xr], vy = V[yr], vf = V[0xF];
		Ctx known = node.In;
		Steps s = new(this);
		if (reachRelays.TryGetValue(a, out Relay? reach)) { s.Add(reach); }
		// Go on to the node for the instruction at `target`
		void Then(int target) => s.Add(Go(node.Id, node.Succ[target]));
		void SetConstant(InstantToggleChain register, int value) { ClearReg(register); s.Add(new Input(BitsOf(value).SelectMany(k => register.Sets[k]))); }
		void ClearReg(InstantToggleChain register) => s.Add(new Input(register.Clears.SelectMany(c => c)));
		void SetFlagFrom(Relay latch) { ClearReg(vf); s.Add(latch.Input); }
		SinkGates SetSink(InstantToggleChain register) => Sink($"set{Array.IndexOf(V, register)}{(register == DelayTimer ? "dt" : "")}", t: k => register.Sets[k]);
		SinkGates AddSink(InstantToggleChain register) => Sink($"add{Array.IndexOf(V, register)}", t: k => register.Inputs[k]);
		// Skip the next instruction when the latch is (or isn't) enabled after `test` has run
		void Skip(Relay latch, bool skipIfLatch)
		{
			int noSkip = a + 2, skip = a + 4;
			Relay ifLatch = NewRelay(startDisabled: true), otherwise = NewRelay();
			(int first, int other) = skipIfLatch ? (noSkip, skip) : (skip, noSkip);
			ifLatch.Output.Connect(otherwise.Disable.Concat(Go(node.Id, node.Succ[other])));
			otherwise.Output.Connect(Go(node.Id, node.Succ[first]));
			(Input arm, Input disarm) = OnLatch(latch, ifLatch);
			s.Add(arm);
			s.Add(latch.Input);
			s.Add(disarm);
			s.Add(otherwise.Input);
			s.Add(otherwise.Enable);
		}
		void AddTempWithCarry(InstantToggleChain into)
		{
			s.Add(CarryGate(into).Enable);
			Apply(s, AddSink(into), t: AllBits);
			s.Add(CarryGate(into).Disable);
		}

		// What has to be written to the machine's registers here (nothing reads the rest before it is written again)
		int needed = MachineLiveAfter(node);
		bool Need(int register) => ((needed >> register) & 1) == 1;
		bool NeedI = Need(16);
		void SetI(int value) { s.Add(clearI); s.Add(new Input(BitsOf(value & 0xFFF, 12).SelectMany(k => IRegister.Sets[k]))); }
		// I moved on by `amount`: by counting when it isn't known (then it is right in the register), else set outright
		void MoveI(int amount) { if (!NeedI) { return; } if (known.HasI) { SetI(known.I + amount); } else { AddToI(s, amount); } }
		(Ctx after, _) = Transfer(a, known);
		// A fused routine, natively (Chip8Native)
		if (FusedAt(a) == FusedRoutine.Danm8kuMoveBullet) { CompileMoveBullet(s, node, needed); }
		else if (FusedAt(a) == FusedRoutine.Danm8kuDrawBullet) { CompileDrawBullet(s, node, needed); }
		else if (FusedAt(a) == FusedRoutine.Danm8kuDrawBullets) { CompileDrawBullets(s, node, needed); }
		else if (FusedAt(a) == FusedRoutine.Danm8kuActBullets) { CompileActBullets(s, node, needed); }
		else if (FusedAt(a) == FusedRoutine.Danm8kuGogoBullet) { CompileGogoBullet(s, node, needed); }
		else if (FusedAt(a) == FusedRoutine.Danm8kuPewPew) { CompilePewPew(s, node, needed); }
		else if (FusedAt(a) == FusedRoutine.Danm8kuUpdatePlayer) { CompileUpdatePlayer(s, node, needed); }
		// A skip or jump table whose outcome is known here: just the one way on
		else if (Decided(node))
		{
			if (node.Succ.Count == 1) { Then(node.Succ.Keys.Single()); }
		}
		// An 8XY_ whose result (and flag) is known here: just set them
		else if (Folded(node, out bool flagged))
		{
			if ((xr != 0xF || !flagged) && Need(xr)) { SetConstant(vx, after.V[xr]); }
			if (flagged && Need(0xF)) { SetConstant(vf, after.V[0xF]); }
			Then(a + 2);
		}
		else if (op >> 12 is 0x6 or 0x7 && !Need(xr)) { Then(a + 2); }
		else if (op >> 12 == 0x7 && known.Has(xr)) { SetConstant(vx, after.V[xr]); Then(a + 2); }
		else if (op >> 12 == 0xA && !NeedI) { Then(a + 2); }
		else if (op >> 12 == 0xF && nn is 0x1E or 0x29 && (!NeedI || known.Has(xr) && (nn == 0x29 || known.HasI)))
		{
			if (NeedI) { SetI(after.I); }
			Then(a + 2);
		}
		else if (op >> 12 == 0xF && nn == 0x07 && !Need(xr) && !timerWaits.Contains(a)) { Then(a + 2); }
		else switch (op >> 12)
		{
			case 0x0 when op == 0x00E0: s.Add(clearScreen); Then(a + 2); break;
			case 0x0 when onStack.Count > 0 || callSitesIn.Count == 0: // 00EE, levelless
				// where it goes isn't known here: give up the whole live path (the Pending handover enables the return node's)
				s.Add(new Input(nextAncestorsOf[node.Id].SelectMany(n => n.Disable)));
				s.Add(new Input((returnsTo.GetValueOrDefault(node.Id) ?? new List<int>()).SelectMany(c => onStack[c].Input)));
				break;
			case 0x0: // 00EE
				s.Add(new Input(nextAncestorsOf[node.Id].SelectMany(n => n.Disable)));
				s.Add(popTop);
				s.Add(lower);
				break;
			case 0x1: Then(nnn); break;
			case 0x2 when onStack.Count > 0:
				s.Add(onStack[node.Id].Enable);
				s.Add(Go(node.Id, node.Callee));
				break;
			case 0x2:
				for (int l = 0; l < StackLevels; l++) { s.Add(setAt[l, siteIndex[node.Id]].Input); }
				s.Add(raise);
				s.Add(Go(node.Id, node.Callee));
				break;
			case 0x3: CompareWithConstant(s, vx, nn); Skip(equalLatch, skipIfLatch: true); break;
			case 0x4: CompareWithConstant(s, vx, nn); Skip(equalLatch, skipIfLatch: false); break;
			case 0x5: CompareRegisters(s, vx, vy); Skip(equalLatch, skipIfLatch: true); break;
			case 0x9: CompareRegisters(s, vx, vy); Skip(equalLatch, skipIfLatch: false); break;
			case 0x6: ClearReg(vx); s.Add(new Input(BitsOf(nn).SelectMany(k => vx.Sets[k]))); Then(a + 2); break;
			case 0x7: s.Add(new Input(BitsOf(nn).SelectMany(k => vx.Inputs[k]))); Then(a + 2); break;
			case 0x8:
				switch (n)
				{
					case 0x0: Copy(s, vy, vx); break;
					case 0x1:
						if (xr != yr) { ProbeOnes(s, OnesOf("or", vy, vx, k => vx.Sets[k]), vy); }
						if (quirks.LogicResetsVF) { ClearReg(vf); }
						break;
					case 0x2:
						if (xr != yr) { ProbeZeros(s, ZerosOf("and", vy, vx, k => vx.Clears[k]), vy); }
						if (quirks.LogicResetsVF) { ClearReg(vf); }
						break;
					case 0x3:
						// XOR: Temp holds VX; each 1 of VY then fires both of Temp's relays for its bit, so a 1 in VX is cleared and a 0 set
						if (xr == yr) { ClearReg(vx); }
						else
						{
							Xor(s, vx, vy);
						}
						if (quirks.LogicResetsVF) { ClearReg(vf); }
						break;
					case 0x6 or 0xE:
					{
						// A shift by one (of VY on the VIP, of VX otherwise); the bit shifted out goes to VF, written last
						bool right = n == 0x6;
						InstantToggleChain from = quirks.ShiftUsesVY ? vy : vx;
						Func<int, Input> shifted = k => right ? (k == 0 ? carryLatch.Enable : vx.Sets[k - 1]) : (k == 7 ? carryLatch.Enable : vx.Sets[k + 1]);
						s.Add(carryLatch.Disable);
						if (from != vx)
						{
							// straight from VY: VX is cleared first, then VY's 1s set the shifted bits
							ClearReg(vx);
							ProbeOnes(s, OnesOf(right ? "shr" : "shl", from, vx, shifted), from);
						}
						else
						{
							// in place, bit by bit away from the bit shifted out: that bit goes to VF, then each bit is cleared and set again
							// from its neighbour (setting doesn't fire a bit's On, so nothing is counted twice)
							var gates = OnesOf(right ? "shr" : "shl", vx, vx, shifted);
							int[] order = right ? Enumerable.Range(0, 8).ToArray() : Enumerable.Range(0, 8).Reverse().ToArray();
							s.Add(gates.open);
							s.Add(vx.Probes[order[0]]);
							for (int i = 0; i < 7; i++) { s.Add(vx.Clears[order[i]]); s.Add(vx.Probes[order[i + 1]]); }
							s.Add(vx.Clears[order[7]]);
							s.Add(gates.close);
						}
						SetFlagFrom(carryLatch);
						break;
					}
					case 0x4:
						s.Add(carryLatch.Disable);
						if (xr != yr)
						{
							// each 1 of VY adds its power of 2 to VX by counting
							s.Add(CarryGate(vx).Enable);
							ProbeOnes(s, OnesOf("add", vy, vx, k => vx.Inputs[k]), vy);
							s.Add(CarryGate(vx).Disable);
						}
						else { Read(s, vy); AddTempWithCarry(vx); }
						SetFlagFrom(carryLatch);
						break;
					case 0x5:
						// VX = VX - VY = VX + ~VY + 1, the flag is the carry out (1 when nothing was borrowed)
						s.Add(carryLatch.Disable);
						if (xr == yr) { ClearReg(vx); s.Add(carryLatch.Enable); }
						else
						{
							// each 0 of VY adds its power of 2, then 1
							s.Add(CarryGate(vx).Enable);
							ProbeZeros(s, ZerosOf("addnot", vy, vx, k => vx.Inputs[k]), vy);
							s.Add(vx.Inputs[0]);
							s.Add(CarryGate(vx).Disable);
						}
						SetFlagFrom(carryLatch);
						break;
					case 0x7:
						s.Add(carryLatch.Disable);
						if (xr == yr) { ClearReg(vx); s.Add(carryLatch.Enable); }
						else
						{
							// VX = VY - VX = VY + ~VX + 1, the flag is the carry out (1 when nothing was borrowed)
							// VX's 0s, noted by not relays, become its 1s; then 1 and VY's 1s are added by counting
							var nots = ZerosOf("not", vx, vx, k => vx.Sets[k]);
							s.Add(nots.arm); s.Add(new Input(vx.Probes.SelectMany(p => p)));
							ClearReg(vx);
							s.Add(nots.fire);
							s.Add(CarryGate(vx).Enable);
							s.Add(vx.Inputs[0]);
							ProbeOnes(s, OnesOf("add", vy, vx, k => vx.Inputs[k]), vy);
							s.Add(CarryGate(vx).Disable);
						}
						SetFlagFrom(carryLatch);
						break;
				}
				Then(a + 2);
				break;
			case 0xA: s.Add(clearI); s.Add(new Input(BitsOf(nnn, 12).SelectMany(k => IRegister.Sets[k]))); Then(a + 2); break;
			case 0xB:
			{
				// A jump into the table at NNN: V0 picks the entry (an even V0 is an entry; anything else stops the machine)
				List<int> table = JumpTable(nnn);
				DecoderRelays pick = Decoder(table.Count * 2, 8, $"jump table {a:X3}#{node.Copy}");
				for (int i = 0; i < table.Count; i++) { if (node.Succ.TryGetValue(table[i], out int entry)) { pick.Candidates[2 * i].Output.Connect(Switch(nextAncestorsOf[node.Id], node.Id, entry, pend: true)); } }
				Read(s, V[0]);
				Decode(s, pick, 8);
				break;
			}
			case 0xC:
				// The counter's entry in the shuffle table, masked, then the counter steps on
				DecodeFrom(s, RandomCounter, RandomDecoder(), 8);
				ClearReg(vx);
				Apply(s, SetSink(vx), t: BitsOf(nn));
				s.Add(RandomCounter.Inputs[0]);
				Then(a + 2);
				break;
			case 0xD:
			{
				// straight from the registers' bits, no Temp
				DecodeFrom(s, vx, xDecoder, 6);
				if (n > 0) { DecodeFrom(s, vy, yDecoders[n].decoder, 5); }
				s.Add(collisionLatch.Disable);
				// Each row: its byte from memory into Temp, then each 1 bit flips its column (bit 7 is the leftmost). When I is known here and
				// the sprite's bytes are never written, the bytes are known too, and each row fires the columns of its 1 bits directly.
				int? spriteI = known.HasI ? known.I : rangeOfI.TryGetValue(a, out var spriteRange) && spriteRange.lo == spriteRange.hi ? spriteRange.lo : null;
				bool constantSprite = spriteI is int at && Enumerable.Range(0, n).All(r => !Ram.Contains((at + r) & 0xFFF));
				int spriteAt = spriteI ?? 0;
				List<Bank> spriteBanks = BanksFor(a, 0, Math.Max(n, 1));
				SinkGates sprite = Sink("sprite", t: k => offsetLines[7 - k].Input);
				if (constantSprite)
				{
					DrawConstantRows(s, Enumerable.Range(0, n).Select(r => (r, (spriteAt + r) & 0xFFF)).Select(t => (t.r, t.Item2 < memory.Length ? (int)memory[t.Item2] : 0)));
				}
				for (int r = 0; r < n && !constantSprite; r++)
				{
					if (spriteI is int rowAt) { s.Add(tempReset); ReadConstant(s, (rowAt + r) & 0xFFF); }
					else { ReadMemory(s, r, spriteBanks, next: r > 0); }
					s.Add(rowLines[r].arm);
					Apply(s, sprite, t: AllBits);
					s.Add(rowLines[r].disarm);
				}
				s.Add(unsetX); if (n > 0) { s.Add(yDecoders[n].unset); }
				SetFlagFrom(collisionLatch);
				Then(a + 2);
				break;
			}
			case 0xE when known.Has(xr):
				// a known key (Danm8ku sets VX just before each test): ask that key's relay directly, no decoding
				s.Add(keyLatch.Disable);
				s.Add(keyHeld[known.V[xr] & 0xF].Input);
				Skip(keyLatch, skipIfLatch: nn == 0x9E);
				break;
			case 0xE:
				// key VX (its low 4 bits): the decoder's candidate asks whether that key is held, which enables the key latch
				Read(s, vx);
				s.Add(keyDecoder.Reset);
				Apply(s, keyDecoder.Sink, t: AllBits.Take(4), n: AllBits.Take(4));
				s.Add(keyLatch.Disable);
				s.Add(keyDecoder.Fire);
				Skip(keyLatch, skipIfLatch: nn == 0x9E);
				break;
			case 0xF when nn == 0x07 && timerWaits.Contains(a):
			{
				// The wait loop FX07, 3X00, 1(this): with the delay timer at 0 this is a plain FX07; otherwise VX = DT, the rest of the frame half
				// is skipped and PC is left where the spinning would have left it, a + 2 * ((tickrate - step) mod 3)
				Relay fuse = NewRelay(startDisabled: true), arm = NewRelay(), disarm = NewRelay(), firePhases = NewRelay();
				var landing = new List<Relay>();
				foreach (int p in new[] { 0, 2 })
				{
					Relay land = NewRelay(startDisabled: true);
					int test = node.Succ[a + 2], jump = nodes[test].Succ[a + 4], back = nodes[jump].Succ[a];
					// the live path is test's by now (the plain FX07 went on to it)
					land.Output.Connect(Next[test].Disable.Concat(Switch(nextAncestorsOf[test], node.Id, p == 0 ? back : jump)));
					phase![p].Output.Connect(land.Input);
					landing.Add(land);
				}
				arm.Output.Connect(new Input(landing.SelectMany(l => l.Enable)));
				disarm.Output.Connect(new Input(landing.SelectMany(l => l.Disable)));
				firePhases.Output.Connect(new Input(phase!.SelectMany(r => r.Input)));
				fuse.Output.Connect(arm.Input.Concat(firePhases.Input).Concat(disarm.Input).Concat(frameGate!.Disable).Concat(closeStepGroups.Input));
				// the copy's gates also arm the fuse: any 1 in the timer
				Relay[] gates = Enumerable.Range(0, 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
				for (int k = 0; k < 8; k++) { DelayTimer.Ons[k].Connect(gates[k].Input); gates[k].Output.Connect(vx.Sets[k].Concat(fuse.Enable)); }
				Relay open = NewRelay(), close = NewRelay();
				open.Output.Connect(new Input(gates.SelectMany(g => g.Enable)));
				close.Output.Connect(new Input(gates.SelectMany(g => g.Disable)));
				ClearReg(vx); s.Add(open); s.Add(new Input(DelayTimer.Probes.SelectMany(p => p))); s.Add(close);
				Then(a + 2);
				s.Add(fuse.Input);
				s.Add(fuse.Disable);
				break;
			}
			case 0xF when nn == 0x07: Copy(s, DelayTimer, vx); Then(a + 2); break;
			case 0xF when nn == 0x15: Copy(s, vx, DelayTimer); Then(a + 2); break;
			case 0xF when nn == 0x0A: Then(a + 2); s.Add(runGate.Disable); s.Add(waitArms[xr]); break;
			case 0xF when nn == 0x1E && known.Has(xr): AddToI(s, known.V[xr]); Then(a + 2); break;   // I isn't known: it is right in the register
			case 0xF when nn == 0x1E: ProbeOnes(s, OnesOf("addI", vx, IRegister, k => IRegister.Inputs[k]), vx); Then(a + 2); break;
			case 0xF when nn == 0x29 && known.Has(xr): s.Add(clearI); s.Add(new Input(BitsOf(quirks.FontAddress + 5 * (known.V[xr] & 0xF), 12).SelectMany(k => IRegister.Sets[k]))); Then(a + 2); break;
			case 0xF when nn == 0x29: Read(s, vx); Decode(s, fontDecoder, 4); Then(a + 2); break;
			case 0xF when nn == 0x33:
				Read(s, vx);
				s.Add(digitReset);
				Decode(s, decimalDecoder, 8);
				List<Bank> digitBanks = BanksFor(a, 0, 3);
				for (int j = 0; j < 3; j++)
				{
					int digit = j;
					Action write = () => { foreach (Relay bit in digits[digit]) { s.Add(bit); } };
					if (known.HasI) { WriteConstant(s, (known.I + j) & 0xFFF, write); }
					else { WriteMemory(s, j, digitBanks, write, next: j > 0); }
				}
				Then(a + 2);
				break;
			case 0xF when nn == 0x55:
				List<Bank> storeBanks = BanksFor(a, 0, xr + 1);
				if (!known.HasI && xr == 7 && toggleRows.Count > 0)
				{
					recordStoreSites.Add(a);
					s.Add(RecordStoreAtI());
					if (quirks.LoadStoreIncrementsI) { MoveI(xr + 1); }
					Then(a + 2);
					break;
				}
				for (int r = 0; r <= xr; r++)
				{
					if (known.HasI) { StoreConstant(s, (known.I + r) & 0xFFF, V[r], known.Has(r) ? known.V[r] : null); continue; }
					Read(s, V[r]);
					WriteMemory(s, r, storeBanks, () => Apply(s, Sink("memwrite", t: AllBankWrites), t: AllBits), next: r > 0);
				}
				if (quirks.LoadStoreIncrementsI) { MoveI(xr + 1); }
				Then(a + 2);
				break;
			case 0xF when nn == 0x65:
			{
				// Only the registers that are live after it. When the next instruction skips on one of them, load that one first and then
				// only what the branch it takes needs (an empty slot of Danm8ku's bullet table needs nothing but its first byte).
				List<Bank> loadBanks = BanksFor(a, 0, xr + 1);
				int loaded = (2 << xr) - 1, wanted = LiveOut(a) & loaded & needed;
				// Load the registers in a mask: from known rows straight into the registers, or through the banks
				void Load(Steps steps, int mask, ref int at)
				{
					if (!known.HasI) { LoadRegisters(steps, loadBanks, mask, ref at); return; }
					for (int r = 0; r <= xr; r++) { if (((mask >> r) & 1) == 1) { LoadConstant(steps, (known.I + r) & 0xFFF, V[r]); } }
				}
				int nextOp = Op(a + 2), t = (nextOp >> 8) & 0xF;
				int last = -1;
				if (nextOp >> 12 is 0x3 or 0x4 && t <= xr && Reachable.Contains(a + 2) && nodes[node.Succ[a + 2]].Succ.Count == 2)
				{
					// what each way on needs, at the nodes the skip goes to (an unrolled copy's empty-slot path needs nothing more, even where the
					// last copy's values flow out of the loop)
					Node skipNode = nodes[node.Succ[a + 2]];
					int equalAt = nextOp >> 12 == 0x3 ? a + 6 : a + 4, otherAt = nextOp >> 12 == 0x3 ? a + 4 : a + 6;
					int ifEqual = machineLive[skipNode.Succ[equalAt]] & loaded & wanted, ifNot = machineLive[skipNode.Succ[otherAt]] & loaded & wanted;
					int first = (ifEqual & ifNot) | (1 << t);
					SplitMasks[node.Id] = (ifEqual, ifNot);
					if (ifEqual != ifNot)
					{
						SplitLoads.Add(a);
						Load(s, first, ref last);
						CompareWithConstant(s, V[t], nextOp & 0xFF);
						Steps equalPath = new(this), otherPath = new(this);
						int lastEqual = last, lastOther = last;
						Load(equalPath, ifEqual & ~first, ref lastEqual);
						Load(otherPath, ifNot & ~first, ref lastOther);
						Relay ifLatch = NewRelay(startDisabled: true), otherwise = NewRelay();
						Input equalStart = equalPath.Relays.Count > 0 ? equalPath.Relays[0].Input : new Input();
						ifLatch.Output.Connect(otherwise.Disable.Concat(equalStart));
						(Input arm, Input disarm) = OnLatch(equalLatch, ifLatch);
						if (otherPath.Relays.Count > 0) { otherwise.Output.Connect(otherPath.Relays[0].Input); }
						ChainSteps(equalPath); ChainSteps(otherPath);
						s.Add(arm); s.Add(equalLatch.Input); s.Add(disarm);
						s.Add(otherwise.Input); s.Add(otherwise.Enable);
						wanted = 0;
					}
				}
				Load(s, wanted, ref last);
				if (quirks.LoadStoreIncrementsI) { MoveI(xr + 1); }
				Then(a + 2);
				break;
			}
		}
		if (s.Relays.Count == 0) { s.Add(new Input()); }
		ChainSteps(s);
		Next[node.Id].Output.Connect(Next[node.Id].Disable.Concat(s.Relays[0].Input));
	}
}
