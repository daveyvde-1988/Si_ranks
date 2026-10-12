using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SiPlayerRanks;

namespace MelonLoader { public static class MelonLogger { public static void Warning(string s)=>Console.WriteLine(s); public static void Msg(string s)=>Console.WriteLine(s); public static void Error(string s)=>Console.WriteLine(s); } }
namespace SiPlayerRanks { public enum RewardKind { Kill,Structure,Logistics,Other } public static class RankMod { public static decimal RewardPolicy(long id,RewardKind kind)=>1m; } }
internal static class RewardPatchProgram
{
    private static int assertions;
    private static void Check(bool valid,string name){assertions++;if(!valid)throw new Exception(name);}
    private static MethodInfo Get(Type t,string name)=>t.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!;
    private static void Field(string name,object value)=>typeof(RewardPatches).GetField(name,BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,value);
    // Read raw MethodBody IL without invoking Harmony's runtime patcher. The installed Melon
    // Harmony build targets Mono, whereas this verification executable runs under .NET 8.
    private static List<CodeInstruction> Read(MethodInfo method)
    {
        var opcodes=typeof(OpCodes).GetFields(BindingFlags.Static|BindingFlags.Public).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
        using var reader=new BinaryReader(new MemoryStream(method.GetMethodBody()!.GetILAsByteArray()!));
        var instructions=new List<(int offset,CodeInstruction code)>();
        while(reader.BaseStream.Position<reader.BaseStream.Length)
        {
            int offset=(int)reader.BaseStream.Position;ushort value=reader.ReadByte();if(value==0xfe)value=(ushort)(0xfe00|reader.ReadByte());
            var op=opcodes[value];object? operand=null;
            switch(op.OperandType)
            {
                case OperandType.InlineNone:break;
                case OperandType.ShortInlineVar:operand=reader.ReadByte();break;
                case OperandType.InlineVar:operand=reader.ReadInt16();break;
                case OperandType.ShortInlineI:operand=reader.ReadSByte();break;
                case OperandType.InlineI:operand=reader.ReadInt32();break;
                case OperandType.InlineI8:operand=reader.ReadInt64();break;
                case OperandType.ShortInlineR:operand=reader.ReadSingle();break;
                case OperandType.InlineR:operand=reader.ReadDouble();break;
                case OperandType.InlineString:operand=method.Module.ResolveString(reader.ReadInt32());break;
                case OperandType.InlineMethod:operand=method.Module.ResolveMethod(reader.ReadInt32());break;
                case OperandType.InlineField:operand=method.Module.ResolveField(reader.ReadInt32());break;
                case OperandType.InlineType:operand=method.Module.ResolveType(reader.ReadInt32());break;
                case OperandType.InlineTok:operand=method.Module.ResolveMember(reader.ReadInt32());break;
                case OperandType.ShortInlineBrTarget:{int delta=reader.ReadSByte();operand=(int)reader.BaseStream.Position+delta;break;}
                case OperandType.InlineBrTarget:{int delta=reader.ReadInt32();operand=(int)reader.BaseStream.Position+delta;break;}
                case OperandType.InlineSwitch:{int count=reader.ReadInt32();var targets=new int[count];for(int i=0;i<count;i++)targets[i]=reader.ReadInt32();int end=(int)reader.BaseStream.Position;operand=targets.Select(t=>t+end).ToArray();break;}
                case OperandType.InlineSig:operand=reader.ReadInt32();break;
                default:throw new Exception("Unsupported operand "+op.OperandType);
            }
            instructions.Add((offset,new CodeInstruction(op,operand)));
        }
        var generator=new DynamicMethod("Labels",typeof(void),Type.EmptyTypes).GetILGenerator();
        var labels=new Dictionary<int,Label>();
        foreach(var item in instructions)
        {
            if(item.code.opcode.OperandType!=OperandType.ShortInlineBrTarget&&item.code.opcode.OperandType!=OperandType.InlineBrTarget)continue;
            int target=(int)item.code.operand;if(!labels.ContainsKey(target))labels[target]=generator.DefineLabel();item.code.operand=labels[target];
        }
        foreach(var item in instructions)if(labels.TryGetValue(item.offset,out var label))item.code.labels.Add(label);
        return instructions.Select(i=>i.code).ToList();
    }
    public static void Main(string[] args)
    {
        string root=Path.GetFullPath(args.Length==0?Directory.GetCurrentDirectory():args[0]);
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>
        {
            string file=new AssemblyName(e.Name).Name+".dll";
            foreach(string dir in new[]{"Mods","Silica_Data/Managed","MelonLoader/net35"})
            {string path=Path.Combine(root,dir,file);if(File.Exists(path))return Assembly.LoadFrom(path);}
            return null;
        };
        Check(RewardPatches.Calculate(100,1.5m,0)==150,"Colonel 50 percent");
        Check(RewardPatches.Calculate(100,1.85m,0)==185,"commander replacement bonus");
        Check(RewardPatches.Calculate(100,0m,0)==0,"Traitor combat cancellation");
        Check(RewardPatches.Calculate(100,.75m,0)==75,"Traitor logistics reduction");
        Check(RewardPatches.Calculate(1,.75m,0)==0,"fractional final award floors");
        Check(RewardPatches.Calculate(100,1.85m,int.MaxValue-10)==10,"overflow saturation");
        Check(RewardPatches.Calculate(100,1m,int.MaxValue)==0,"full balance");
        Check(RewardPatches.Calculate(-100,1.85m,0)==-100,"negative amount untouched");
        var koh=Assembly.LoadFrom(Path.Combine(root,"Mods/Si_KingOfTheHill.dll")).GetType("Si_KingOfTheHill.KingOfTheHill")!;
        var add=Get(koh,"AddCredits");Field("addCredits",add);Field("getCredits",koh.GetMethod("GetCredits",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(long)},null)!);
        foreach(string name in new[]{"OnUnitDestroyed_Reward","OnStructureDestroyed_Reward","GrantZonePresenceReward"})
        {
            var method=Get(koh,name);var original=Read(method);
            var output=RewardPatches.CombatTranspiler(original,method).ToList();
            Check(output.Count==original.Count+4,name+" surgical insertion");
            Check(output.Count(c=>c.Calls(add))==1,name+" retains exactly one original payment");
            Check(output.Count(c=>c.operand is MethodInfo m&&m.DeclaringType==typeof(RewardPatches))==1,name+" exactly one multiplier");
            Check(output.Count(c=>c.opcode.Name!.StartsWith("stloc"))==original.Count(c=>c.opcode.Name!.StartsWith("stloc"))+1,name+" updates the amount used by original messages");
        }
        var external=Get(koh,"GrantExternalCredits");var ext=Read(external);
        var modified=RewardPatches.ExternalTranspiler(ext).ToList();
        Check(modified.Count==ext.Count+5,"external surgical insertion");
        Check(modified.Count(c=>c.Calls(add))==1,"external single original payment");
        Check(modified.FindIndex(c=>c.operand is MethodInfo m&&m.Name=="External")>modified.FindIndex(c=>c.opcode==OpCodes.Ret),"external modifier after early validation return");
        var logistics=Assembly.LoadFrom(Path.Combine(root,"Mods/Si_Logistics.dll")).GetType("Si_Logistics.LogisticsMod")!;
        var grant=Get(logistics.GetNestedType("KohCredits",BindingFlags.NonPublic)!,"TryGrant");Field("logisticsGrant",grant);
        var due=Get(logistics,"PayDueRewards");var originalDue=Read(due);
        var modifiedDue=RewardPatches.LogisticsChatTranspiler(originalDue,due).ToList();
        Check(modifiedDue.Count==originalDue.Count+1,"chat-only replacement");
        int call=originalDue.FindIndex(c=>c.Calls(grant));
        Check(originalDue.Take(call+1).Select(c=>c.ToString()).SequenceEqual(modifiedDue.Take(call+1).Select(c=>c.ToString())),"pending deduction and grant operands unchanged");
        Check(modifiedDue.Count(c=>c.Calls(grant))==1,"original grant intact");
        foreach(string name in new[]{"PayBountyIfApplicable","GrantStarterCreditsToAll","TryBuy"})
        {
            var method=Get(koh,name);if(method==null)continue;
            Check(!Read(method).Any(c=>c.operand is MethodInfo m&&m.DeclaringType==typeof(RewardPatches)),"excluded "+name);
        }
        bool refused=false;try{RewardPatches.ExternalTranspiler(new[]{new CodeInstruction(OpCodes.Ret)}).ToList();}catch(InvalidOperationException){refused=true;}
        Check(refused,"incompatible IL rejected");
        Console.WriteLine("PASS: "+assertions+" reward compatibility assertions; current installed DLL IL read offline, no game methods executed.");
    }
}
