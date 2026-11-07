package dev.switchboard.send;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.zip.*;

final class AndroidPayloads {
    static final class Staged { final File file;final PeerEngine.Source source;Staged(File f,String name,String mime){file=f;source=new PeerEngine.Source(name,mime,f.length(),()->new FileInputStream(f));} }
    static File cache(Context context)throws IOException{File root=new File(context.getCacheDir(),"outgoing");if(!root.isDirectory()&&!root.mkdirs())throw new IOException("缓存目录不可用");return root;}
    static boolean owns(Context context,Uri uri){try{return "file".equals(uri.getScheme())&&new File(uri.getPath()).getCanonicalPath().startsWith(cache(context).getCanonicalPath()+File.separator);}catch(Exception e){return false;}}
    static void clean(Context context,Uri uri){if(owns(context,uri))new File(uri.getPath()).delete();}
    static Staged text(Context context,String text)throws Exception{
        byte[] bytes=text.getBytes(StandardCharsets.UTF_8);if(text.trim().isEmpty())throw new IOException("文本为空");if(bytes.length>PayloadArchive.MAX_TEXT)throw new IOException("文本上限 1 MB");
        File file=File.createTempFile("text-",".txt",cache(context));try(OutputStream out=new FileOutputStream(file)){out.write(bytes);}return new Staged(file,"文本.txt",PayloadArchive.TEXT);
    }
    static Staged folder(Context context,Uri tree)throws Exception{
        ContentResolver resolver=context.getContentResolver();String id=DocumentsContract.isDocumentUri(context,tree)?DocumentsContract.getDocumentId(tree):DocumentsContract.getTreeDocumentId(tree);
        Uri document=DocumentsContract.buildDocumentUriUsingTree(tree,id);String name=displayName(resolver,document);
        File file=File.createTempFile("folder-",".zip",cache(context));final int[] count={0};final long[] total={0};Set<String> visited=new HashSet<>();
        try(ZipOutputStream zip=new ZipOutputStream(new FileOutputStream(file))){
            zip.setLevel(Deflater.BEST_SPEED);addDirectory(resolver,tree,id,"",zip,visited,count,total);
        }catch(Exception error){file.delete();throw error;}
        return new Staged(file,name+".zip",PayloadArchive.FOLDER);
    }
    private static void addDirectory(ContentResolver resolver,Uri tree,String id,String relative,ZipOutputStream zip,Set<String> visited,int[] count,long[] total)throws Exception{
        PayloadArchive.check(()->false);if(!visited.add(id))throw new IOException("文件夹包含循环引用");
        if(!relative.isEmpty()){if(++count[0]>PayloadArchive.MAX_ENTRIES)throw new IOException("文件夹条目超过 10000");zip.putNextEntry(new ZipEntry(relative+"/"));zip.closeEntry();}
        Uri children=DocumentsContract.buildChildDocumentsUriUsingTree(tree,id);
        try(Cursor cursor=resolver.query(children,new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_MIME_TYPE},null,null,null)){
            if(cursor==null)throw new IOException("文件夹无法读取");
            while(cursor.moveToNext()){
                PayloadArchive.check(()->false);String child=cursor.getString(0),name=cursor.getString(1),mime=cursor.getString(2);
                if(name==null||!PayloadArchive.validSegment(name))throw new IOException("文件夹名称不兼容："+name);
                String path=relative.isEmpty()?name:relative+"/"+name;
                if(DocumentsContract.Document.MIME_TYPE_DIR.equals(mime))addDirectory(resolver,tree,child,path,zip,visited,count,total);
                else{
                    if(++count[0]>PayloadArchive.MAX_ENTRIES)throw new IOException("文件夹条目超过 10000");zip.putNextEntry(new ZipEntry(path));
                    try(InputStream in=resolver.openInputStream(DocumentsContract.buildDocumentUriUsingTree(tree,child))){if(in==null)throw new IOException("文件无法读取");byte[] buffer=new byte[131072];int n;while((n=in.read(buffer))!=-1){PayloadArchive.check(()->false);total[0]+=n;if(total[0]>PeerEngine.MAX)throw new IOException("文件夹超过 32 GB");zip.write(buffer,0,n);}}
                    zip.closeEntry();
                }
            }
        }
    }
    static String displayName(ContentResolver resolver,Uri document)throws Exception{
        try(Cursor cursor=resolver.query(document,new String[]{DocumentsContract.Document.COLUMN_DISPLAY_NAME},null,null,null)){if(cursor!=null&&cursor.moveToFirst()){String name=cursor.getString(0);if(name!=null&&!name.isEmpty())return name;}}
        throw new IOException("文件夹名称不可用");
    }
    static String readText(Context context,PeerEngine.Item item)throws Exception{return PayloadArchive.text(context.getContentResolver().openInputStream(Uri.parse(item.uri)));}
    static Uri receiveTree(Context context){String value=context.getSharedPreferences("send",0).getString("folderDirectory","");return value.isEmpty()?null:Uri.parse(value);}
}
