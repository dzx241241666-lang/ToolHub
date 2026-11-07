package dev.switchboard.send;

import java.io.*;
import java.nio.ByteBuffer;
import java.nio.charset.*;
import java.util.*;
import java.util.function.BooleanSupplier;
import java.util.zip.*;

/** Payload format helpers shared with the JVM interoperability tests. */
public final class PayloadArchive {
    public static final String TEXT="application/vnd.toolhub.text+utf8", FOLDER="application/vnd.toolhub.folder+zip";
    public static final int MAX_TEXT=1024*1024, MAX_ENTRIES=10000;
    public static String label(String name,String mime){return TEXT.equals(mime)?"文本":FOLDER.equals(mime)?name.replaceFirst("(?i)\\.zip$","")+" · 文件夹":name;}
    public static void check(BooleanSupplier cancelled)throws IOException{if(Thread.currentThread().isInterrupted()||cancelled.getAsBoolean())throw new IOException("传输已取消");}
    public static String text(InputStream input)throws Exception {
        try(InputStream in=input;ByteArrayOutputStream out=new ByteArrayOutputStream()){
            byte[] buffer=new byte[8192];int n;while((n=in.read(buffer))!=-1){if(out.size()+n>MAX_TEXT)throw new IOException("文本上限 1 MB");out.write(buffer,0,n);}
            return StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(out.toByteArray())).toString();
        }
    }
    public static List<ZipEntry> inspect(ZipFile zip)throws Exception {
        List<ZipEntry> result=new ArrayList<>();Set<String> names=new HashSet<>();long total=0;
        Enumeration<? extends ZipEntry> entries=zip.entries();
        while(entries.hasMoreElements()){
            ZipEntry entry=entries.nextElement();String path=entry.getName();if(path.endsWith("/"))path=path.substring(0,path.length()-1);
            if(result.size()>=MAX_ENTRIES||path.isEmpty()||path.length()>2048||path.contains("\\"))throw new IOException("文件夹路径无效");
            for(String segment:path.split("/",-1))if(!validSegment(segment))throw new IOException("文件夹路径无效");
            if(!names.add(path.toLowerCase(Locale.ROOT))||entry.getSize()<0||entry.getSize()>PeerEngine.MAX-total)throw new IOException("文件夹条目无效或超过 32 GB");
            total+=entry.getSize();result.add(entry);
        }
        return result;
    }
    public static boolean validSegment(String name){return !name.isEmpty()&&!name.equals(".")&&!name.equals("..")&&name.equals(name.trim())&&!name.matches("(?s).*[\\x00-\\x1f<>:\"/\\\\|?*].*")&&!name.endsWith(".")&&!name.endsWith(" ")&&!name.split("\\.",2)[0].matches("(?i)CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]");}
    public static void copy(InputStream source,OutputStream target,long expected,BooleanSupplier cancelled)throws Exception {
        byte[] buffer=new byte[131072];long count=0;int n;
        while((n=source.read(buffer))!=-1){check(cancelled);count+=n;if(count>expected)throw new IOException("文件夹内容长度无效");target.write(buffer,0,n);}
        if(count!=expected)throw new IOException("文件夹内容未完整接收");
    }
}
