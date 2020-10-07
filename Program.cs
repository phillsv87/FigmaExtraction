using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigmaExtraction
{
    class Program
    {

        static string TokenPath;
        static string FileId;
        static string NodeIds;

        static async Task Main(string[] args)
        {

            try{
                var cancelSource=new CancellationTokenSource();
                var cancel=cancelSource.Token;
                
                string themeFile=null;
                string infName=null;

                for(int i=0;i<args.Length;i++){
                    
                    switch(args[i].ToLower())
                    {
                        
                        case "-themefile":
                            themeFile=args[++i];
                            break;
                        
                        case "-infname":
                            infName=args[++i];
                            break;
                        
                        case "-tokenpath":
                            TokenPath=args[++i];
                            break;
                        
                        case "-fileid":
                            FileId=args[++i];
                            break;
                        
                        case "-nodeids":
                            NodeIds=args[++i];
                            break;
                    }
                }

                if(themeFile==null){
                    throw new InvalidOperationException("themeFile required");
                }

                if(infName==null){
                    throw new InvalidOperationException("infName required");
                }

                await ConvertAsync(themeFile,infName,cancel);


                

            }catch(Exception ex){
                Console.WriteLine(ex.Message);
                Environment.Exit(1);
            }


        }

        const string NameKey="themeName";

        static async Task ConvertAsync(string themeFile, string infName, CancellationToken cancel)
        {

            var results=new List<Dictionary<string,object>>();
            List<string> keys;

            var themes=JsonConvert.DeserializeObject<Dictionary<string,Dictionary<string,object>>>(
                await File.ReadAllTextAsync(themeFile,cancel));
                

            foreach(var themePair in themes){

                var theme=themePair.Value;
                var themeName=themePair.Key;

                theme[NameKey]=themeName;
                results.Add(theme);

                if(theme.TryGetValue("@extend",out object _extend)){
                    var extend=_extend as string;
                    if(extend!=null){
                        var baseTheme=results.FirstOrDefault(t=>t[NameKey].Equals(extend));
                        if(baseTheme==null){
                            throw new FormatException($"base theme {extend} not found or defined after extending theme {themeName}");
                        }
                        foreach(var pair in baseTheme){
                            if(pair.Key==NameKey || pair.Key.StartsWith("@") || theme.ContainsKey(pair.Key)){
                                continue;
                            }
                            theme[pair.Key]=pair.Value;
                        }
                    }
                }

                keys=theme.Keys.ToList();
                foreach(var key in keys){
                    if(key.StartsWith("@")){
                        continue;
                    }

                    var value=theme[key];
                    if(value is string){
                        theme[key]=await ComputeValueAsync(theme,(string)value,cancel);

                    }
                }

            }


            if(results.Count==0){
                throw new FormatException("No themes defined");
            }

            var buffer=new StringBuilder();

            buffer.Append($"export interface {infName}\n{{\n");
            var infTheme=results[0];
            keys=infTheme.Keys.ToList();
            foreach(var key in keys){
                if(key.StartsWith("@")){
                    continue;
                }
                string type;
                var value=infTheme[key];
                if(value is string){
                    type="string";
                }else if(IsNumber(value)){
                    type="number";
                }else{
                    type="any";
                }
                buffer.Append($"    {key}:{type};\n");
            }
            buffer.Append("}\n\n");

            foreach(var theme in results){
                buffer.Append($"export const {theme[NameKey]}Theme:{infName}=\n{{\n");

                keys=theme.Keys.ToList();
                foreach(var key in keys){
                    if(key.StartsWith("@")){
                        continue;
                    }
                    buffer.Append($"    {key}:{JsonConvert.SerializeObject(theme[key])},\n");
                }

                buffer.Append("}\n\n");
            }


            Console.WriteLine(buffer.ToString());


        }

        static bool IsNumber(object value){
            if(value==null){
                return false;
            }
            var type=value.GetType();
            return 
                type.Equals(typeof(Int32)) ||
                type.Equals(typeof(Int64)) ||
                type.Equals(typeof(Double)) ||
                type.Equals(typeof(Single));
        }

        private static Regex VarReg=new Regex(@"\{(\w+)\}");

        static async Task<object> ComputeValueAsync(Dictionary<string,object> theme, string value, CancellationToken cancel)
        {

            while(true){
                var replaced=VarReg.Replace(value,m=>
                {
                    var name=m.Groups[1].Value;
                    if(!theme.TryGetValue(name,out object rv)){
                        throw new FormatException("No theme var found by name "+name);
                    }
                    var rs=rv as string;
                    if(rs!=null && rs.StartsWith("=")){
                        rv=$"( {rs.Substring(1)} )";
                        rv=rs;
                    }
                    return rv?.ToString();
                });
                if(replaced==value){
                    break;
                }
                value=replaced;
            }

            if(value.StartsWith('=')){
                return await EvalAsync(value.Substring(1),cancel);
            }else{
                return value;
            }

        }

        static readonly Regex FuncReg=new Regex(@"([a-zA-Z]+)\((.*)");
        static async Task<object> EvalAsync(string value, CancellationToken cancel)
        {
            var startingValue=value;
            object result=value;
            var argParts=new List<string>();

            var match=FuncReg.Match(value);
            if(!match.Success){
                return value;
            }

            var func=match.Groups[1].Value;
            var argsStr=match.Groups[2].Value;
            string end="";
            int p=1;
            int argI=0;
            for(int i=0;i<argsStr.Length;i++){
                var ch=argsStr[i];
                if(ch=='('){
                    p++;
                }else if(ch==')'){
                    p--;
                    if(p==0){
                        argParts.Add(argsStr.Substring(argI,i-argI));
                        if(i<argsStr.Length-1){
                            end=argsStr.Substring(i+1);
                        }
                        argsStr=argsStr.Substring(0,i);
                        break;
                    }
                }else if(ch==','){
                    if(p==1){
                        argParts.Add(argsStr.Substring(argI,i-argI));
                        argI=i+1;
                    }
                }
            }
            if(p!=0){
                throw new FormatException("Expected an ending ) character - "+startingValue);
            }

            
            var args=new List<object>();
            foreach(var arg in argParts){
                var a=arg.Trim();
                if(a==string.Empty){
                    args.Add(null);
                    continue;
                }
                args.Add(await EvalAsync(a,cancel));
            }

            switch(func){
                case "get":
                    result=await GetAsync(args,cancel);
                    break;

                case "hex":
                    result=ConvertToHex(args);
                    break;

                case "math":
                    result=ConvertMath(args);
                    break;

                default:
                    throw new Exception($"No func by the name {func} found");
            }

            return result;
        }

        static string ConvertToHex(List<object> args)
        {
            if(args.Count!=1){
                throw new Exception("hex func accepts a JObject as its only parameter");
            }

            var fills=args[0] as JArray;
            if(fills==null){
                throw new Exception("hex func accepts a JObject as its only parameter");
            }

            var color=
                fills.FirstOrDefault(t=>(t as JObject)?["type"]?.ToObject<string>()=="SOLID")
                ?["color"] as JObject;
            if(color==null){
                throw new Exception("hex func - Solid color expected");
            }

            var r=color["r"].ToObject<double>();
            var g=color["g"].ToObject<double>();
            var b=color["b"].ToObject<double>();
            var a=color["a"].ToObject<double>();

            var hex=
                ((int)Math.Round(255*r)).ToString("X2")+
                ((int)Math.Round(255*g)).ToString("X2")+
                ((int)Math.Round(255*b)).ToString("X2");
            if(a<1){
                hex+=((int)Math.Round(255*a)).ToString("X2");
            }

            return "#"+hex;

        }

        static double ConvertMath(List<object> args){
            if(args.Count!=1){
                throw new Exception("math func accepts a mathamatical expression as its only parameter");
            }
            return Convert.ToDouble(new DataTable().Compute(args[0].ToString(), null));
        }


        static JObject FigFile=null;

        static async Task<object> GetAsync(List<object> args, CancellationToken cancel)
        {
            if(FigFile==null){
                await LoadFigFileAsync(cancel);
            }

            var obj=(JObject)FigFile["document"];
            for(int i=0;i<args.Count;i++){
                var arg=args[i] as string;
                if(string.IsNullOrWhiteSpace(arg)){
                    continue;
                }
                if(arg.StartsWith('#')){
                    obj=FindNode(obj,arg.Substring(1));
                }else{
                    var jo=obj[arg];
                    if(i==args.Count-1){
                        return jo;
                    }
                    obj=jo as JObject;
                }
                if(obj==null){
                    throw new Exception("get() path argument does not point to a JObject - "+arg);
                }
            }

            return obj;

        }

        static JObject FindNode(JObject parent, string name){

            var children=parent["children"] as JArray;
            if(children==null){
                return null;
            }

            foreach(var child in children){
                var obj=child as JObject;
                if(obj==null){
                    continue;
                }
                if((obj["name"] as JValue)?.ToObject<string>()==name){
                    return obj;
                }
                var sub=FindNode(obj,name);
                if(sub!=null){
                    return sub;
                }
            }
            return null;
        }

        static async Task LoadFigFileAsync(CancellationToken cancel)
        {
            if(FileId==null){
                throw new Exception("-fileId required");
            }
            if(TokenPath==null){
                throw new Exception("-tokenPath required");
            }
            if(NodeIds==null){
                throw new Exception("-nodeId required");
            }

            if(!File.Exists(TokenPath)){
                throw new Exception("No token file found at "+TokenPath);
            }
            var token=await File.ReadAllTextAsync(TokenPath,cancel);

            using(var http=new HttpClient())
            {
                http.DefaultRequestHeaders.Add("X-FIGMA-TOKEN",token);
                var url=$"https://api.figma.com/v1/files/{FileId}?ids={NodeIds}";
                Console.Error.WriteLine($"GET {url}");
                var json=await http.GetStringAsync(url);
                Console.Error.WriteLine($"Figma File Length {json.Length/1000}KB");

                FigFile=JsonConvert.DeserializeObject<JObject>(json);
            }

        }
    }
}
