// Deterministic, throttled loopback fixture for manual/desktop end-to-end QA.
import http from 'node:http';
import { createHash } from 'node:crypto';
const payload=Buffer.alloc(16*1024*1024);
for(let i=0;i<payload.length;i++)payload[i]=(i*31+Math.floor(i/251)+7)%256;
const hash=createHash('sha256').update(payload).digest('hex');
let requests=0;
const server=http.createServer(async(req,res)=>{
  if(req.url==='/health'){res.setHeader('Content-Type','application/json');res.end(JSON.stringify({requests,sha256:hash,bytes:payload.length}));return;}
  if(!req.url?.startsWith('/Flux-e2e.bin')){res.writeHead(404);res.end();return;}
  requests++;
  const match=/^bytes=(\d+)-(\d*)$/.exec(req.headers.range||'');
  let start=0,end=payload.length-1;
  if(match){start=Number(match[1]);end=match[2]?Number(match[2]):end;if(start>end||end>=payload.length){res.writeHead(416,{'Content-Range':`bytes */${payload.length}`});res.end();return;}}
  res.writeHead(match?206:200,{'Content-Type':'application/octet-stream','Content-Length':end-start+1,'Accept-Ranges':'bytes','ETag':'"flux-ui-fixture-v1"',...(match?{'Content-Range':`bytes ${start}-${end}/${payload.length}`}:{})});
  for(let offset=start;offset<=end&&!res.destroyed;offset+=65536){res.write(payload.subarray(offset,Math.min(end+1,offset+65536)));await new Promise(resolve=>setTimeout(resolve,100));}
  res.end();
});
server.listen(0,'127.0.0.1',()=>console.log(JSON.stringify({url:`http://127.0.0.1:${server.address().port}/Flux-e2e.bin`,sha256:hash,bytes:payload.length})));
process.on('SIGINT',()=>server.close(()=>process.exit(0)));
