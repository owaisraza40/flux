import "./build.mjs";
import {readFile,writeFile,readdir,mkdir} from "node:fs/promises";
import {resolve,dirname,relative} from "node:path";
import {fileURLToPath} from "node:url";
import {deflateRawSync} from "node:zlib";
import {createPublicKey,createHash,sign,verify} from "node:crypto";

const root=resolve(dirname(fileURLToPath(import.meta.url)),"..");
const project=resolve(root,"..");
const output=resolve(project,"artifacts","release");
await mkdir(output,{recursive:true});
function crc32(bytes){let crc=-1;for(const byte of bytes){crc^=byte;for(let i=0;i<8;i++)crc=(crc>>>1)^(-(crc&1)&0xedb88320);}return(crc^-1)>>>0;}
async function files(directory){const entries=[];for(const item of await readdir(directory,{withFileTypes:true})){const path=resolve(directory,item.name);if(item.isDirectory())entries.push(...await files(path));else entries.push(path);}return entries.sort();}
async function zip(directory){const local=[],central=[];let offset=0,count=0;for(const path of await files(directory)){
  const name=Buffer.from(relative(directory,path).replaceAll("\\","/"));const data=await readFile(path);const compressed=deflateRawSync(data);const crc=crc32(data);
  const header=Buffer.alloc(30);header.writeUInt32LE(0x04034b50);header.writeUInt16LE(20,4);header.writeUInt16LE(0x800,6);header.writeUInt16LE(8,8);header.writeUInt16LE(0x5c21,12);header.writeUInt32LE(crc,14);header.writeUInt32LE(compressed.length,18);header.writeUInt32LE(data.length,22);header.writeUInt16LE(name.length,26);
  local.push(header,name,compressed);const entry=Buffer.alloc(46);entry.writeUInt32LE(0x02014b50);entry.writeUInt16LE(20,4);entry.writeUInt16LE(20,6);entry.writeUInt16LE(0x800,8);entry.writeUInt16LE(8,10);entry.writeUInt16LE(0x5c21,14);entry.writeUInt32LE(crc,16);entry.writeUInt32LE(compressed.length,20);entry.writeUInt32LE(data.length,24);entry.writeUInt16LE(name.length,28);entry.writeUInt32LE(offset,42);central.push(entry,name);offset+=header.length+name.length+compressed.length;count++;
}const directoryBytes=Buffer.concat(central);const end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50);end.writeUInt16LE(count,8);end.writeUInt16LE(count,10);end.writeUInt32LE(directoryBytes.length,12);end.writeUInt32LE(offset,16);return Buffer.concat([...local,directoryBytes,end]);}
function varint(value){const bytes=[];while(value>=128){bytes.push((value&127)|128);value=Math.floor(value/128);}bytes.push(value);return Buffer.from(bytes);}
function field(number,data){return Buffer.concat([varint(number*8+2),varint(data.length),data]);}
const privateKey=await readFile(resolve(project,".local","keys","flux-extension.pem"),"utf8");
const publicKey=createPublicKey(privateKey).export({type:"spki",format:"der"});
const archive=await zip(resolve(root,"chromium"));
const signedHeader=field(1,createHash("sha256").update(publicKey).digest().subarray(0,16));
const signedLength=Buffer.alloc(4);signedLength.writeUInt32LE(signedHeader.length);
const signedBytes=Buffer.concat([Buffer.from("CRX3 SignedData\0"),signedLength,signedHeader,archive]);
const signature=sign("sha256",signedBytes,privateKey);
if(!verify("sha256",signedBytes,createPublicKey(privateKey),signature))throw new Error("CRX signature verification failed");
const proof=Buffer.concat([field(1,publicKey),field(2,signature)]);
const header=Buffer.concat([field(2,proof),field(10000,signedHeader)]);
const prefix=Buffer.alloc(12);prefix.write("Cr24");prefix.writeUInt32LE(3,4);prefix.writeUInt32LE(header.length,8);
await writeFile(resolve(output,"Flux-Chromium-1.0.0.crx"),Buffer.concat([prefix,header,archive]));
await writeFile(resolve(output,"Flux-Chromium-1.0.0.zip"),archive);
await writeFile(resolve(output,"Flux-Firefox-1.0.0.xpi"),await zip(resolve(root,"firefox")));
console.log("Packaged and cryptographically self-verified Flux CRX3; packaged unsigned Firefox XPI. Private signing key remains under .local/keys.");
