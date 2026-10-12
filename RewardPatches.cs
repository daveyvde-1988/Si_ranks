using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using MelonLoader;

namespace SiPlayerRanks
{
    // Temporary compatibility layer. Only verified earning sites are patched; AddCredits itself
    // is never patched. No purchase, starter-credit, bounty, refund or transfer path is modified.
    internal static class RewardPatches
    {
        private const string KohHash="5377B71A034E065DF76D5A3F595DE733DAF24D9A875DEF9D0C092BCCAAABF8BC";
        private const string LogisticsHash="B6A363CF4CADCBED16C1F65B7F403D8BED65D0CE5B7B2679F82E35B5F32AAAF5";
        private const string LogisticsReason="Logistics rewards (batched)";
        private static MethodInfo? addCredits,getCredits,logisticsGrant;
        private static readonly HashSet<MethodBase> installed=new();
        private static readonly Dictionary<string,string> statuses=new();
        [ThreadStatic] private static long lastLogisticsId;
        [ThreadStatic] private static int lastLogisticsBase,lastLogisticsPaid;
        [ThreadStatic] private static bool hasLogisticsReceipt;
        private static bool provenanceWarning;
        public static string Status=>statuses.Count==0?"Reward patches pending":string.Join("; ",statuses.Select(p=>p.Key+": "+p.Value));

        public static void Unbind(HarmonyLib.Harmony harmony)
        {
            foreach(var method in installed.ToArray())
            {
                string transpiler=method.Name=="PayDueRewards"?nameof(LogisticsChatTranspiler):method.Name=="GrantExternalCredits"?nameof(ExternalTranspiler):nameof(CombatTranspiler);
                // Method-specific removal leaves all other ranks and third-party patches intact.
                try{harmony.Unpatch(method,Helper(transpiler));installed.Remove(method);}
                catch(Exception e){RankLog.Warning("Ranks reward patch removal failed for "+method.Name+": "+e.GetBaseException().Message);}
            }
            hasLogisticsReceipt=false;lastLogisticsId=0;lastLogisticsBase=0;lastLogisticsPaid=0;
            statuses.Clear();provenanceWarning=false;
        }

        public static void Bind(HarmonyLib.Harmony harmony)
        {
            var koh=Find("Si_KingOfTheHill.KingOfTheHill");
            if(koh==null){statuses["KoH"]="unavailable";return;}
            if(!Verified(koh.Assembly,KohHash)){Warn("KoH","DLL changed; reward patches disabled until reviewed");return;}
            addCredits=Method(koh,"AddCredits",typeof(void),typeof(long),typeof(int));
            getCredits=Method(koh,"GetCredits",typeof(int),typeof(long));
            if(addCredits==null||getCredits==null){Warn("KoH","credit signatures incompatible");return;}
            Install(harmony,Method(koh,"OnUnitDestroyed_Reward",typeof(void),typeof(Unit),typeof(UnityEngine.GameObject)),"kills",nameof(CombatTranspiler));
            Install(harmony,Method(koh,"OnStructureDestroyed_Reward",typeof(void),typeof(Structure),typeof(UnityEngine.GameObject)),"structures",nameof(CombatTranspiler));
            Install(harmony,Method(koh,"GrantZonePresenceReward",typeof(void),typeof(Player),typeof(int),typeof(string)),"zone presence",nameof(CombatTranspiler));
            var logistics=Find("Si_Logistics.LogisticsMod");
            if(logistics==null){statuses["Logistics"]="unavailable";return;}
            if(!Verified(logistics.Assembly,LogisticsHash)){Warn("Logistics","DLL changed; logistics reward patch disabled until reviewed");return;}
            var bridge=logistics.GetNestedType("KohCredits",BindingFlags.NonPublic);
            logisticsGrant=bridge?.GetMethod("TryGrant",BindingFlags.Static|BindingFlags.Public,null,new[]{typeof(long),typeof(int),typeof(string)},null);
            if(logisticsGrant==null||!logisticsGrant.ReturnType.IsEnum){Warn("Logistics","credit bridge signature incompatible");return;}
            // Validate chat BEFORE patching payments, so an incompatible announcement is never left
            // reporting the base amount while the balance receives a different amount.
            var due=Method(logistics,"PayDueRewards",typeof(void));
            if(due==null){Warn("Logistics","payout signature incompatible");return;}
            Install(harmony,due,"Logistics announcement",nameof(LogisticsChatTranspiler));
            if(!installed.Contains(due))return;
            Install(harmony,Method(koh,"GrantExternalCredits",typeof(bool),typeof(long),typeof(int),typeof(string)),"Logistics payment",nameof(ExternalTranspiler));
        }

        private static Type? Find(string name)=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(name,false)).FirstOrDefault(t=>t!=null);
        private static MethodInfo? Method(Type type,string name,Type result,params Type[] args)
        {
            var m=type.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic,null,args,null);
            return m?.ReturnType==result?m:null;
        }
        private static bool Verified(Assembly assembly,string hash)
        {
            try{using var stream=File.OpenRead(assembly.Location);using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")==hash;}
            catch{return false;}
        }
        private static void Warn(string site,string reason)
        {
            string previous=statuses.TryGetValue(site,out var value)?value:"";
            statuses[site]=reason;
            if(previous!=reason)RankLog.Warning("Ranks reward patch "+site+": "+reason+". Original rewards remain in use.");
        }
        private static void Install(HarmonyLib.Harmony harmony,MethodInfo? target,string site,string transpiler)
        {
            if(target==null){Warn(site,"method missing");return;}
            if(installed.Contains(target))return;
            try
            {
                harmony.Patch(target,transpiler:new HarmonyMethod(typeof(RewardPatches),transpiler));
                installed.Add(target);statuses[site]="active";
                RankLog.Info("Ranks reward patch active: "+site);
            }
            catch(Exception e){Warn(site,"incompatible IL: "+e.GetBaseException().Message);}
        }

        // Apply after KoH calculates the base integer. Floor positive credits deterministically;
        // clamp to remaining int balance room so KoH's unchecked addition cannot wrap to zero.
        internal static int Calculate(int amount,decimal multiplier,int balance)
        {
            if(amount<=0)return amount;
            decimal paid=decimal.Floor(amount*Math.Max(0m,multiplier));
            decimal room=Math.Max(0L,(long)int.MaxValue-Math.Max(0,balance));
            return (int)Math.Min(paid,room);
        }
        private static int Scale(long id,int amount,RewardKind kind)
        {
            if(amount<=0)return amount;
            try{return Calculate(amount,RankMod.RewardPolicy(id,kind),(int)getCredits!.Invoke(null,new object[]{id})!);}
            catch(Exception e){Warn("policy","calculation unavailable: "+e.GetBaseException().Message);return amount;}
        }
        private static int Kill(long id,int amount)=>Scale(id,amount,RewardKind.Kill);
        private static int Structure(long id,int amount)=>Scale(id,amount,RewardKind.Structure);
        private static int Zone(long id,int amount)=>Scale(id,amount,RewardKind.Other);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int External(long id,int amount,string reason)
        {
            if(reason!=LogisticsReason||amount<=0)return amount;
            hasLogisticsReceipt=false;
            // The reason alone is not sufficient: unknown API callers with the same text retain
            // their original payment. A verified Logistics bridge must actually be on this stack.
            if(logisticsGrant==null||!(new StackTrace().GetFrames()?.Any(f=>f.GetMethod()==logisticsGrant)??false))
            {
                statuses["Logistics payment"]="matching reason without verified caller; payment unchanged";
                if(!provenanceWarning){provenanceWarning=true;RankLog.Warning("Ranks: matching external reason without verified Logistics caller; payment left unchanged.");}
                return amount;
            }
            int paid=Scale(id,amount,RewardKind.Logistics);
            statuses["Logistics payment"]="active";
            lastLogisticsId=id;lastLogisticsBase=amount;lastLogisticsPaid=paid;hasLogisticsReceipt=true;
            return paid;
        }
        private static string LogisticsDisplay(long id,int amount)
        {
            int paid=hasLogisticsReceipt&&id==lastLogisticsId&&amount==lastLogisticsBase?lastLogisticsPaid:amount;
            hasLogisticsReceipt=false;
            return paid.ToString(CultureInfo.InvariantCulture);
        }

        private static MethodInfo Helper(string name)=>AccessTools.Method(typeof(RewardPatches),name);
        private static int LocalIndex(CodeInstruction instruction)
        {
            if(instruction.opcode==OpCodes.Ldloc_0)return 0;
            if(instruction.opcode==OpCodes.Ldloc_1)return 1;
            if(instruction.opcode==OpCodes.Ldloc_2)return 2;
            if(instruction.opcode==OpCodes.Ldloc_3)return 3;
            if(instruction.opcode!=OpCodes.Ldloc&&instruction.opcode!=OpCodes.Ldloc_S&&instruction.opcode!=OpCodes.Ldloca&&instruction.opcode!=OpCodes.Ldloca_S)return -1;
            return instruction.operand is LocalBuilder local?local.LocalIndex:Convert.ToInt32(instruction.operand,CultureInfo.InvariantCulture);
        }
        private static void Require(bool valid,string message){if(!valid)throw new InvalidOperationException(message);}
        private static CodeInstruction LocalInstruction(bool store,int index,object? operand)
        {
            if(index<4)
            {
                var codes=store?new[]{OpCodes.Stloc_0,OpCodes.Stloc_1,OpCodes.Stloc_2,OpCodes.Stloc_3}:new[]{OpCodes.Ldloc_0,OpCodes.Ldloc_1,OpCodes.Ldloc_2,OpCodes.Ldloc_3};
                return new CodeInstruction(codes[index]);
            }
            // Preserve Harmony's own LocalBuilder when it is present; raw-IL tests use indices.
            return new CodeInstruction(store?OpCodes.Stloc:OpCodes.Ldloc,operand is LocalBuilder?operand:(short)index);
        }
        private static int CreditSite(List<CodeInstruction> code)
        {
            var sites=code.Select((c,i)=>(c,i)).Where(x=>x.c.Calls(addCredits)).Select(x=>x.i).ToArray();
            Require(sites.Length==1,"expected exactly one direct AddCredits call");return sites[0];
        }
        private static void InsertAt(List<CodeInstruction> code,int index,List<CodeInstruction> added)
        {
            // Existing branch targets must execute the modifier, too.
            Require(code[index].blocks.Count==0,"unexpected exception boundary at payment operand");
            added[0].MoveLabelsFrom(code[index]);code.InsertRange(index,added);
        }
        internal static IEnumerable<CodeInstruction> CombatTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
        {
            var code=instructions.ToList();int site=CreditSite(code);Require(site>=2,"missing payment operands");
            var amount=code[site-1];int local=LocalIndex(amount);
            Require(local>=0&&amount.opcode!=OpCodes.Ldloca&&amount.opcode!=OpCodes.Ldloca_S,"amount must be an integer local load");
            Require(__originalMethod.GetMethodBody()!.LocalVariables[local].LocalType==typeof(int),"amount local is not int");
            var id=code[site-2];int idLocal=LocalIndex(id);
            Require(idLocal>=0?__originalMethod.GetMethodBody()!.LocalVariables[idLocal].LocalType==typeof(long):id.opcode==OpCodes.Call&&id.operand is MethodInfo m&&m.Name=="GetPlayerSteamId"&&m.ReturnType==typeof(long),"unrecognized account-id operand");
            string helper=__originalMethod.Name=="OnUnitDestroyed_Reward"?nameof(Kill):__originalMethod.Name=="OnStructureDestroyed_Reward"?nameof(Structure):__originalMethod.Name=="GrantZonePresenceReward"?nameof(Zone):throw new InvalidOperationException("unrecognized reward handler");
            InsertAt(code,site-1,new List<CodeInstruction>{new(OpCodes.Dup),new(amount.opcode,amount.operand),new(OpCodes.Call,Helper(helper)),LocalInstruction(true,local,amount.operand)});
            return code;
        }
        internal static IEnumerable<CodeInstruction> ExternalTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code=instructions.ToList();int site=CreditSite(code);
            Require(site>=2&&code[site-2].opcode==OpCodes.Ldarg_0&&code[site-1].opcode==OpCodes.Ldarg_1,"external payment arguments changed");
            // Original ModeBuyActive / valid account / positive BASE checks have already passed.
            // Setting the final amount to zero here keeps KoH's successful return and consumes the
            // original Logistics batch exactly once, without refunding it for endless retries.
            InsertAt(code,site-1,new List<CodeInstruction>{new(OpCodes.Dup),new(OpCodes.Ldarg_1),new(OpCodes.Ldarg_2),new(OpCodes.Call,Helper(nameof(External))),new(OpCodes.Starg_S,(byte)1)});
            return code;
        }
        internal static IEnumerable<CodeInstruction> LogisticsChatTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
        {
            var code=instructions.ToList();var calls=code.Select((c,i)=>(c,i)).Where(x=>x.c.Calls(logisticsGrant)).Select(x=>x.i).ToArray();
            Require(calls.Length==1,"expected one Logistics grant call");int grant=calls[0];
            Require(grant>=3&&code[grant-1].opcode==OpCodes.Ldstr&&(string)code[grant-1].operand==LogisticsReason,"Logistics reason changed");
            int id=LocalIndex(code[grant-3]),amount=LocalIndex(code[grant-2]);
            Require(id>=0&&amount>=0&&__originalMethod.GetMethodBody()!.LocalVariables[id].LocalType==typeof(long)&&__originalMethod.GetMethodBody()!.LocalVariables[amount].LocalType==typeof(int),"Logistics grant operands changed");
            var messages=code.Select((c,i)=>(c,i)).Where(x=>x.c.opcode==OpCodes.Ldstr&&Equals(x.c.operand," credits for logistics")).Select(x=>x.i).ToArray();
            Require(messages.Length==1,"Logistics announcement changed");int text=messages[0];
            Require(text>grant&&text>=2&&LocalIndex(code[text-2])==amount&&(code[text-2].opcode==OpCodes.Ldloca||code[text-2].opcode==OpCodes.Ldloca_S)&&code[text-1].operand is MethodInfo tostring&&tostring.DeclaringType==typeof(int)&&tostring.Name=="ToString","Logistics amount display changed");
            // Access label count through non-generic collection: the installed net35 Harmony's
            // Label type is not exposed by the netstandard mscorlib reference facade.
            int labelCount=((System.Collections.ICollection)typeof(CodeInstruction).GetField("labels")!.GetValue(code[text-1])!).Count;
            Require(code[text-2].blocks.Count==0&&code[text-1].blocks.Count==0&&labelCount==0,"unexpected announcement control flow");
            var loadId=LocalInstruction(false,id,code[grant-3].operand);loadId.MoveLabelsFrom(code[text-2]);
            var loadAmount=LocalInstruction(false,amount,code[text-2].operand);
            code.RemoveRange(text-2,2);code.InsertRange(text-2,new[]{loadId,loadAmount,new CodeInstruction(OpCodes.Call,Helper(nameof(LogisticsDisplay)))});
            return code;
        }
    }
}
