using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SiPlayerRanks
{
    // Formation owns recruitment enforcement. This adapter owns only its named policy.
    internal sealed class FormationAdapter
    {
        private const string Owner="SilicaFieldRanks";
        private MethodInfo? request, clearOwner, readPolicy, readLimit;
        private PropertyInfo? enabled;
        private RankConfig config=new();
        private readonly Dictionary<long,string> applied=new();
        private readonly Dictionary<long,string> status=new();
        public void Configure(RankConfig value)=>config=value;
        public void Bind()
        {
            var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Si_Formation.FormationApi",false)).FirstOrDefault(t=>t!=null);
            request=type?.GetMethod("RequestPlayerPolicy",new[]{typeof(ulong),typeof(string),typeof(int),typeof(string[]),typeof(string[]),typeof(string[])});
            clearOwner=type?.GetMethod("ClearOwnerPolicies",new[]{typeof(string)});
            readPolicy=type?.GetMethod("TryGetPlayerPolicy");readLimit=type?.GetMethod("TryGetPlayerLimit");
            enabled=type?.GetProperty("GlobalEnabled");
        }
        private string[] Categories(Privileges policy)
        {
            if(policy.Categories==UnitCategories.Any)return new[]{"*"};
            return config.NativeCategoryRules.Where(p=>Enum.TryParse(p.Key,out UnitCategories category)&&(policy.Categories&category)!=0)
                .SelectMany(p=>p.Value).Distinct(StringComparer.Ordinal).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
        }
        private static string Signature(int limit,string[] categories,string[] allowed,string[] denied)=>limit+"|"+string.Join(",",categories)+"|"+string.Join(",",allowed)+"|"+string.Join(",",denied);
        public void Update(long id,Privileges policy)
        {
            int limit=Math.Max(1,policy.Squad);var categories=Categories(policy);
            var allowed=config.NativeObjectCategoryOverrides.Where(p=>(policy.Categories&p.Value)!=0).Select(p=>p.Key).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
            var denied=policy.ExcludedObjectInfoNames.Concat(config.NativeObjectCategoryOverrides.Where(p=>(policy.Categories&p.Value)==0).Select(p=>p.Key))
                .Distinct(StringComparer.Ordinal).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
            string signature=Signature(limit,categories,allowed,denied);
            if(request==null){Bind();if(request==null){status[id]="Formation 2.10.0 policy API unavailable";return;}}
            try
            {
                // Formation may clear a policy on reset or disconnect. Verify ownership
                // before treating a previously successful request as still applied.
                if(applied.TryGetValue(id,out string? previous)&&previous==signature&&readPolicy!=null)
                {
                    object?[] args={unchecked((ulong)id),null,0,null,null,null};
                    string result=readPolicy.Invoke(null,args)?.ToString()??"Unknown";
                    if((result=="Success"||result=="AdminOverride")&&(string?)args[1]==Owner&&
                        Signature((int)args[2]!,args[3] as string[]??Array.Empty<string>(),args[4] as string[]??Array.Empty<string>(),args[5] as string[]??Array.Empty<string>())==signature)
                    {status[id]=result;return;}
                }
                string update=request.Invoke(null,new object[]{unchecked((ulong)id),Owner,limit,categories,allowed,denied})?.ToString()??"Unknown";
                status[id]=update;
                if(update=="Success"||update=="AdminOverride")applied[id]=signature;
            }
            catch(Exception e){status[id]="Formation API error: "+e.GetBaseException().Message;}
        }
        public string Describe(long id)
        {
            if(request==null||readLimit==null)return "Formation policy API unavailable; rank squad permissions inactive.";
            try
            {
                object[] args={unchecked((ulong)id),0};string result=readLimit.Invoke(null,args)?.ToString()??"Unknown";
                bool running=enabled?.GetValue(null) as bool? ?? false;
                string policyStatus=status.TryGetValue(id,out var value)?value:"pending";
                return result=="Success"?$"Formation effective cap: {args[1]} including player; {(running?"enabled":"disabled/native fallback")}; rank policy: {policyStatus}."
                    :"Formation status: "+result+"; rank policy: "+policyStatus+".";
            }
            catch{return "Formation policy status unavailable.";}
        }
        public void ForgetApplications()=>applied.Clear();
        public void ClearAll()
        {
            try{clearOwner?.Invoke(null,new object[]{Owner});}catch{}
            applied.Clear();status.Clear();
        }
    }
}
