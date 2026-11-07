package dev.switchboard.send;

import org.json.*;
import java.io.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.net.*;
import java.security.cert.X509Certificate;
import javax.net.ssl.*;

public final class Interop {
    public static void main(String[] args)throws Exception{
        Path root=Paths.get(args[1]);KeyStore keys=KeyStore.getInstance("PKCS12");try(InputStream in=Files.newInputStream(Paths.get(args[0]))){keys.load(in,"test-only".toCharArray());}
        KeyManagerFactory factory=KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());factory.init(keys,"test-only".toCharArray());SSLContext tls=SSLContext.getInstance("TLS");tls.init(factory.getKeyManagers(),null,null);
        String pin=PeerEngine.hex(MessageDigest.getInstance("SHA-256").digest(keys.getCertificate("interop").getEncoded()));
        Path received=root.resolve("java-received");Files.createDirectories(received);Files.write(received.resolve("样例.txt"),"existing file".getBytes("UTF-8"));
        PeerEngine.Storage storage=(name,mime,size)->{Path partial=Files.createTempFile(received,"partial-",".tmp");return new PeerEngine.Sink(){
            public OutputStream stream()throws Exception{return Files.newOutputStream(partial);}
            public PeerEngine.Item commit()throws Exception{Path target=received.resolve(name);int dot=name.lastIndexOf('.');String stem=dot>0?name.substring(0,dot):name;String ext=dot>0?name.substring(dot):"";for(int i=1;Files.exists(target);i++)target=received.resolve(stem+" ("+i+")"+ext);Files.move(partial,target);return new PeerEngine.Item(target.getFileName().toString(),target.toString(),mime,size);}
            public void abort(){try{Files.deleteIfExists(partial);}catch(Exception ignored){}}
        };};
        CompletableFuture<List<PeerEngine.Item>> arrived=new CompletableFuture<>();
        AtomicBoolean reject=new AtomicBoolean();
        PeerEngine.Events events=new PeerEngine.Events(){
            public void offer(PeerEngine.Offer offer){if(reject.get())offer.reject();else offer.accept(true);}
            public void received(String peer,List<PeerEngine.Item> items,boolean copy){if(copy)arrived.complete(items);else arrived.completeExceptionally(new Exception("copy option lost"));}
            public void progress(String a,String b,String c,long d,long e){}
            public void notice(String message){System.err.println(message);}
        };
        try(PeerEngine engine=new PeerEngine((args.length>4?args[4]:UUID.randomUUID().toString()),"Android core interop","Android",pin,tls,storage,events,args.length>3?Integer.parseInt(args[3]):0,"127.0.0.1",false)){
            engine.start();Files.write(root.resolve("java-ready.json"),new JSONObject().put("port",engine.self.port).toString().getBytes("UTF-8"));
            if(args.length>2&&args[2].equals("--ui")){
                List<PeerEngine.Item> items=arrived.get(600,TimeUnit.SECONDS);
                if(items.size()!=1)throw new Exception("UI send file count mismatch");
                byte[] expected=Files.readAllBytes(root.resolve("source/ui-test.txt"));
                byte[] actual=Files.readAllBytes(Paths.get(items.get(0).uri));
                if(!Arrays.equals(expected,actual))throw new Exception("UI send checksum mismatch");
                Files.write(root.resolve("ui-result.json"),new JSONObject().put("singleDeviceClickSent",true).put("sha256Matched",true).put("personalFilesSent",false).toString().getBytes("UTF-8"));
                Thread.sleep(1000);return;
            }
            Path pcReady=root.resolve("pc-ready.json");long end=System.currentTimeMillis()+45000;while(!Files.exists(pcReady)&&System.currentTimeMillis()<end)Thread.sleep(100);
            JSONObject pc=new JSONObject(new String(Files.readAllBytes(pcReady),"UTF-8"));PeerEngine.Peer peer=engine.addPeer(pc.getString("address"));
            List<PeerEngine.Source> sources=new ArrayList<>();try(java.util.stream.Stream<Path> stream=Files.list(root.resolve("source"))){stream.forEach(path->{try{sources.add(new PeerEngine.Source(path.getFileName().toString(),"application/octet-stream",Files.size(path),()->Files.newInputStream(path)));}catch(Exception e){throw new RuntimeException(e);}});}
            engine.send(peer,sources);List<PeerEngine.Item> items=arrived.get(35,TimeUnit.SECONDS);if(items.size()!=sources.size())throw new Exception("count mismatch");
            for(PeerEngine.Source source:sources){String expected=source.name.equals("样例.txt")?"样例 (1).txt":source.name;PeerEngine.Item item=items.stream().filter(i->i.name.equals(expected)).findFirst().get();byte[] actual=Files.readAllBytes(Paths.get(item.uri));byte[] original=Files.readAllBytes(root.resolve("source").resolve(source.name));if(!MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(actual),MessageDigest.getInstance("SHA-256").digest(original)))throw new Exception("checksum mismatch");}
            if(!new String(Files.readAllBytes(received.resolve("样例.txt")),"UTF-8").equals("existing file"))throw new Exception("file overwritten");
            String api="https://127.0.0.1:"+engine.self.port+PeerEngine.API;
            request(api+"/upload?session=bad&file=bad&token=bad",new byte[]{1},403);
            JSONObject metadata=new JSONObject().put("sender",peer.json()).put("files",new JSONArray().put(new JSONObject().put("id","guarded").put("name","guarded.bin").put("mime","application/octet-stream").put("size",3).put("sha256",String.join("",Collections.nCopies(64,"0")))));
            byte[] body=metadata.toString().getBytes("UTF-8");JSONObject prepared=new JSONObject(request(api+"/prepare",body,200));
            String upload=api+"/upload?session="+prepared.getString("session")+"&file=guarded&token=";
            request(upload+"wrong",new byte[]{1,2,3},403);
            request(upload+prepared.getJSONObject("files").getString("guarded"),new byte[]{1,2,3},422);
            if(Files.exists(received.resolve("guarded.bin")))throw new Exception("Corrupt file published");
            request(api+"/cancel?session="+prepared.getString("session"),new byte[0],200);
            request(upload+prepared.getJSONObject("files").getString("guarded"),new byte[]{1,2,3},403);
            JSONObject localCancel=new JSONObject(request(api+"/prepare",body,200));engine.cancelReceive();
            request(api+"/upload?session="+localCancel.getString("session")+"&file=guarded&token="+localCancel.getJSONObject("files").getString("guarded"),new byte[]{1,2,3},403);
            reject.set(true);request(api+"/prepare",body,403);
            try(java.util.stream.Stream<Path> list=Files.list(received)){if(list.anyMatch(p->p.getFileName().toString().startsWith("partial-")))throw new Exception("Temporary file leaked");}
            Files.write(root.resolve("java-result.json"),new JSONObject().put("success",true).put("collisionPreserved",true).put("sha256Matched",true).put("badChecksumRejected",true).put("cancelledSessionRejected",true).put("wrongTokenRejected",true).put("receiveRejection",true).toString().getBytes("UTF-8"));
            // Give the final HTTP upload response time to leave the socket before shutdown.
            Thread.sleep(500);
        }
    }
    private static String request(String url,byte[] data,int expected)throws Exception{
        SSLContext tls=SSLContext.getInstance("TLS");tls.init(null,new TrustManager[]{new X509TrustManager(){public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}public void checkClientTrusted(X509Certificate[] chain,String auth){}public void checkServerTrusted(X509Certificate[] chain,String auth){}}},null);
        HttpsURLConnection c=(HttpsURLConnection)new URL(url).openConnection(Proxy.NO_PROXY);c.setSSLSocketFactory(tls.getSocketFactory());c.setHostnameVerifier((a,b)->true);c.setConnectTimeout(3000);c.setReadTimeout(5000);c.setRequestMethod("POST");c.setDoOutput(true);c.setFixedLengthStreamingMode(data.length);c.setRequestProperty("Content-Type","application/json");
        try{try(OutputStream out=c.getOutputStream()){out.write(data);}int status=c.getResponseCode();if(status!=expected)throw new Exception("Expected "+expected+", got "+status+" for "+url);try(InputStream in=status==200?c.getInputStream():c.getErrorStream()){return in==null?"":new String(in.readAllBytes(),"UTF-8");}}finally{c.disconnect();}
    }
}
