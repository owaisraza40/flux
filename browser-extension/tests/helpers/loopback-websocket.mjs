// Test-only masked WebSocket client using Node BCL, with an extension Origin.
import net from "node:net";
import {randomBytes,createHash} from "node:crypto";

export class LoopbackWebSocket extends EventTarget {
  static OPEN=1;static CLOSED=3;readyState=0;
  constructor(url,origin){
    super();this.buffer=Buffer.alloc(0);this.upgraded=false;this.fragments=[];
    const parsed=new URL(url);if(parsed.hostname!=="localhost"&&parsed.hostname!=="127.0.0.1")throw new Error("Test socket only supports loopback");
    this.key=randomBytes(16).toString("base64");this.socket=net.createConnection({host:"127.0.0.1",port:Number(parsed.port)||6543});
    this.socket.once("connect",()=>this.socket.write(`GET / HTTP/1.1\r\nHost: localhost:${parsed.port||6543}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: ${this.key}\r\nOrigin: ${origin}\r\n\r\n`));
    this.socket.on("data",bytes=>{try{this.accept(bytes);}catch(error){this.failure(error);}});
    this.socket.on("error",error=>this.failure(error));
    this.socket.on("close",()=>{this.readyState=3;this.dispatchEvent(new Event("close"));});
  }
  failure(error){const event=new Event("error");event.error=error;this.dispatchEvent(event);this.socket.destroy();}
  accept(bytes){
    this.buffer=Buffer.concat([this.buffer,bytes]);
    if(!this.upgraded){const end=this.buffer.indexOf("\r\n\r\n");if(end<0){if(this.buffer.length>16384)throw new Error("Oversized handshake");return;}
      const headers=this.buffer.subarray(0,end).toString("ascii");this.buffer=this.buffer.subarray(end+4);
      const expected=createHash("sha1").update(this.key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
      const accept=headers.split("\r\n").find(line=>line.toLowerCase().startsWith("sec-websocket-accept:"))?.split(":").slice(1).join(":").trim();
      if(!headers.startsWith("HTTP/1.1 101 ")||accept!==expected)throw new Error("Flux refused WebSocket handshake");
      this.upgraded=true;this.readyState=1;this.dispatchEvent(new Event("open"));
    }
    while(this.buffer.length>=2){const first=this.buffer[0],second=this.buffer[1];if(second&128)throw new Error("Server frame must not be masked");let length=second&127,offset=2;
      if(length===126){if(this.buffer.length<4)return;length=this.buffer.readUInt16BE(2);offset=4;}
      else if(length===127){if(this.buffer.length<10)return;const long=this.buffer.readBigUInt64BE(2);if(long>1048576n)throw new Error("Oversized server frame");length=Number(long);offset=10;}
      if(length>1048576)throw new Error("Oversized server frame");if(this.buffer.length<offset+length)return;
      const payload=this.buffer.subarray(offset,offset+length);this.buffer=this.buffer.subarray(offset+length);const opcode=first&15;
      if(opcode===8){this.close();return;}if(opcode===9){this.writeFrame(10,payload);continue;}if(opcode===10)continue;
      if(opcode!==0&&opcode!==1)throw new Error("Nontext server frame");this.fragments.push(payload);
      if(this.fragments.reduce((sum,value)=>sum+value.length,0)>1048576)throw new Error("Oversized message");
      if(first&128){const event=new Event("message");event.data=Buffer.concat(this.fragments).toString("utf8");this.fragments=[];this.dispatchEvent(event);}
    }
  }
  writeFrame(opcode,payload){const size=payload.length,extended=size>=126?2:0,mask=randomBytes(4),header=Buffer.alloc(2+extended+4);header[0]=128|opcode;header[1]=128|(extended?126:size);if(extended)header.writeUInt16BE(size,2);mask.copy(header,2+extended);const data=Buffer.alloc(size);for(let i=0;i<size;i++)data[i]=payload[i]^mask[i%4];this.socket.write(Buffer.concat([header,data]));}
  send(value){if(this.readyState!==1)throw new Error("WebSocket is not open");const bytes=Buffer.from(value);if(bytes.length>65535)throw new Error("Test message too large");this.writeFrame(1,bytes);}
  close(){if(this.readyState===1){this.writeFrame(8,Buffer.from([3,232]));this.socket.end();}else this.socket.destroy();this.readyState=3;}
}
