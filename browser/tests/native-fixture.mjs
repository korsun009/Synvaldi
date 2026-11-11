// A temporary host name, registry value and named pipe keep both test profiles
// isolated from any installed Synvaldi agent. The native host source is the shipped one.
import fs from "node:fs/promises";
import net from "node:net";
import path from "node:path";
import {fileURLToPath} from "node:url";
import {execFileSync} from "node:child_process";
import {once} from "node:events";
const pause=ms=>new Promise(r=>setTimeout(r,ms));

export async function nativeFixture(root) {
  const nonce=crypto.randomUUID().replaceAll("-","");
  const host="com.synvaldi.test_"+nonce, name="Synvaldi.Test."+nonce;
  const source=fileURLToPath(new URL("../../desktop/NativeHost/",import.meta.url));
  const build=path.join(root,"native-build");
  await fs.mkdir(build);
  await fs.copyFile(path.join(source,"Synvaldi.NativeHost.csproj"),path.join(build,"Synvaldi.NativeHost.csproj"));
  await fs.writeFile(path.join(build,"Program.cs"),(await fs.readFile(path.join(source,"Program.cs"),"utf8")).replace('"Synvaldi.Agent.v1"',JSON.stringify(name)));
  const output=path.join(build,"published");
  execFileSync("dotnet",["build",path.join(build,"Synvaldi.NativeHost.csproj"),"-o",output],{stdio:"pipe",windowsHide:true});
  const manifest=path.join(build,"native.json");
  await fs.writeFile(manifest,JSON.stringify({name:host,description:"Synvaldi isolated integration",path:path.join(output,"Synvaldi.NativeHost.exe"),type:"stdio",
    allowed_origins:["chrome-extension://jcfipkamaklpdghopicgfllhnigfjcio/"]}));
  const server=net.createServer();
  const clients=[];
  const registration="HKCU\\Software\\Google\\Chrome\\NativeMessagingHosts\\"+host;
  const sockets=new Set();
  let relay=false;
  server.on("connection",socket=>{
    sockets.add(socket);socket.on("close",()=>sockets.delete(socket));socket.on("error",()=>{});
    let buffer=Buffer.alloc(0);
    const client={messages:[],send(message){
      const body=Buffer.from(JSON.stringify(message));
      const size=Buffer.alloc(4);size.writeUInt32LE(body.length);
      socket.write(Buffer.concat([size,body]));
    }};
    socket.on("data",chunk=>{
      buffer=Buffer.concat([buffer,chunk]);
      while(buffer.length>=4){
        const length=buffer.readUInt32LE();
        if(length<1||length>1048576){socket.destroy();return;}
        if(buffer.length<length+4)break;
        const msg=JSON.parse(buffer.subarray(4,4+length).toString());
        buffer=buffer.subarray(4+length);
        client.messages.push(msg);
        if(msg.type==="hello"){clients.push(client);client.send({type:"config",epoch:nonce,enabled:true,selection:"all",capture:false});}
        if(msg.type==="changes"&&relay){
          client.send({type:"received",batchId:msg.batchId});
          for(const dest of clients.filter(c=>c!==client))dest.send({type:"apply",batchId:msg.batchId,changes:msg.changes});
        }
      }
    });
  });
  server.listen("\\\\.\\pipe\\"+name);await once(server,"listening");
  try {execFileSync("reg.exe",["add",registration,"/ve","/t","REG_SZ","/d",manifest,"/f"],{windowsHide:true});}
  catch(error){server.close();throw error;}
  return {host,clients,epoch:nonce,relay(){relay=true;},async request(client,msg){
    const id=crypto.randomUUID();client.send({...msg,batchId:id});
    const deadline=Date.now()+60000;
    while(Date.now()<deadline){const reply=client.messages.find(m=>m.batchId===id);if(reply){if(reply.ok===false)throw Error(reply.error);return reply;}await pause(80);}
    throw Error("Timed out waiting for native "+msg.type);
  },close(){
    for(const socket of sockets)socket.destroy();server.close();
    execFileSync("reg.exe",["delete",registration,"/f"],{windowsHide:true});
  }};
}

