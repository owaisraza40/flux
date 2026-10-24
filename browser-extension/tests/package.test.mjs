import {test} from "node:test";
import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import {createPublicKey,createHash,verify} from "node:crypto";
import {inflateRawSync} from "node:zlib";

function fields(bytes){const result=new Map();let offset=0;function variable(){let value=0,shift=0;while(true){const byte=bytes[offset++];value+=(byte&127)*2**shift;if(!(byte&128))return value;shift+=7;assert.ok(shift<=28);}}while(offset<bytes.length){const tag=variable();assert.equal(tag&7,2);const length=variable();result.set(tag>>3,bytes.subarray(offset,offset+length));offset+=length;}return result;}
function entries(bytes){const result=new Map();let offset=0;while(bytes.readUInt32LE(offset)===0x04034b50){const compressedLength=bytes.readUInt32LE(offset+18),size=bytes.readUInt32LE(offset+22),nameLength=bytes.readUInt16LE(offset+26),extraLength=bytes.readUInt16LE(offset+28);const name=bytes.subarray(offset+30,offset+30+nameLength).toString();const start=offset+30+nameLength+extraLength;const data=inflateRawSync(bytes.subarray(start,start+compressedLength));assert.equal(data.length,size);result.set(name,data);offset=start+compressedLength;}return result;}

test("CRX3 signature, public identity and packaged manifest are consistent",async()=>{
  const crx=await readFile(new URL("../../artifacts/release/Flux-Chromium-1.0.0.crx",import.meta.url));assert.equal(crx.subarray(0,4).toString(),"Cr24");assert.equal(crx.readUInt32LE(4),3);
  const headerLength=crx.readUInt32LE(8),header=fields(crx.subarray(12,12+headerLength)),proof=fields(header.get(2)),signed=header.get(10000),zip=crx.subarray(12+headerLength);
  const publicKey=proof.get(1),signature=proof.get(2),length=Buffer.alloc(4);length.writeUInt32LE(signed.length);
  const message=Buffer.concat([Buffer.from("CRX3 SignedData\0"),length,signed,zip]);
  assert.equal(verify("sha256",message,createPublicKey({key:publicKey,format:"der",type:"spki"}),signature),true);
  assert.deepEqual(fields(signed).get(1),createHash("sha256").update(publicKey).digest().subarray(0,16));
  const files=entries(zip),manifest=JSON.parse(files.get("manifest.json"));assert.equal(manifest.key,publicKey.toString("base64"));assert.ok(files.has("content.js"));assert.ok(files.has("options.html"));
  assert.ok(![...files.keys()].some(name=>/\.pem$|\.local|tests\//.test(name)));
});
test("Firefox XPI is a ZIP with root manifest and explicit stable add-on ID",async()=>{
  const files=entries(await readFile(new URL("../../artifacts/release/Flux-Firefox-1.0.0.xpi",import.meta.url)));const manifest=JSON.parse(files.get("manifest.json"));
  assert.equal(manifest.browser_specific_settings.gecko.id,"flux@flux.local");assert.equal(manifest.version,"1.0.0");assert.ok(files.has("background.mjs"));assert.ok(files.has("content.js"));assert.ok(!files.has("META-INF/mozilla.rsa"));
});
