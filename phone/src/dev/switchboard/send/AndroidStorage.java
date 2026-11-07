package dev.switchboard.send;

import android.content.*;
import android.net.Uri;
import android.os.Environment;
import android.provider.MediaStore;
import android.provider.DocumentsContract;
import android.webkit.MimeTypeMap;
import java.io.*;
import java.util.Locale;
import java.util.*;
import java.util.function.BooleanSupplier;
import java.util.zip.*;

final class AndroidStorage implements PeerEngine.Storage {
    private final ContentResolver resolver;
    private final Context context;
    AndroidStorage(Context context) { this.context=context;resolver=context.getContentResolver(); }
    @Override public PeerEngine.Sink create(String name,String mime,long size)throws Exception {
        if(PayloadArchive.FOLDER.equals(mime))return folder(name);
        String inferred=null;int dot=name.lastIndexOf('.');if(dot>=0)inferred=MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(dot+1).toLowerCase(Locale.ROOT));
        final boolean text=PayloadArchive.TEXT.equals(mime);
        final String type=text?"text/plain":(mime.isEmpty()||mime.equals("application/octet-stream"))&&inferred!=null?inferred:mime.isEmpty()?"application/octet-stream":mime;
        ContentValues values=new ContentValues();
        values.put(MediaStore.Downloads.DISPLAY_NAME,name);
        values.put(MediaStore.Downloads.MIME_TYPE,type);
        values.put(MediaStore.Downloads.RELATIVE_PATH,Environment.DIRECTORY_DOWNLOADS+"/ToolHub Send/");
        values.put(MediaStore.Downloads.IS_PENDING,1);
        Uri uri=resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI,values);
        if(uri==null)throw new IOException("接收目录不可用");
        return new PeerEngine.Sink(){
            public OutputStream stream()throws Exception{OutputStream out=resolver.openOutputStream(uri,"w");if(out==null)throw new IOException("文件无法写入");return out;}
            public PeerEngine.Item commit()throws Exception{if(text)PayloadArchive.text(resolver.openInputStream(uri));ContentValues done=new ContentValues();done.put(MediaStore.Downloads.IS_PENDING,0);if(resolver.update(uri,done,null,null)!=1)throw new IOException("文件保存失败");return new PeerEngine.Item(name,uri.toString(),type,size,text?"text":"file");}
            public void abort(){try{resolver.delete(uri,null,null);}catch(Exception ignored){}}
        };
    }
    private PeerEngine.Sink folder(String name)throws Exception {
        File part=File.createTempFile("received-folder-",".zip",context.getCacheDir());
        return new PeerEngine.Sink(){
            Uri created;
            public OutputStream stream()throws Exception{return new FileOutputStream(part);}
            public PeerEngine.Item commit()throws Exception{return commit(()->false);}
            public PeerEngine.Item commit(BooleanSupplier cancelled)throws Exception{
                Uri tree=AndroidPayloads.receiveTree(context);if(tree==null)throw new IOException("文件夹接收目录未设置");
                Uri parent=DocumentsContract.buildDocumentUriUsingTree(tree,DocumentsContract.getTreeDocumentId(tree));
                try(ZipFile zip=new ZipFile(part)){
                    List<ZipEntry> entries=PayloadArchive.inspect(zip);PayloadArchive.check(cancelled);
                    Set<String> names=new HashSet<>(),ids=new HashSet<>();
                    Uri children=DocumentsContract.buildChildDocumentsUriUsingTree(tree,DocumentsContract.getDocumentId(parent));
                    try(android.database.Cursor cursor=resolver.query(children,new String[]{DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_DOCUMENT_ID},null,null,null)){
                        if(cursor==null)throw new IOException("接收目录无法读取");while(cursor.moveToNext()){names.add(cursor.getString(0).toLowerCase(Locale.ROOT));ids.add(cursor.getString(1));}
                    }
                    String base=name.toLowerCase(Locale.ROOT).endsWith(".zip")?name.substring(0,name.length()-4):name;if(base.isEmpty())base="文件夹";
                    String unique=base;for(int i=1;names.contains(unique.toLowerCase(Locale.ROOT));i++){if(i>10000)throw new IOException("同名文件夹过多");unique=base+" ("+i+")";}
                    Uri candidate=DocumentsContract.createDocument(resolver,parent,DocumentsContract.Document.MIME_TYPE_DIR,unique);
                    if(candidate==null||ids.contains(DocumentsContract.getDocumentId(candidate))||DocumentsContract.getDocumentId(candidate).equals(DocumentsContract.getDocumentId(parent)))throw new IOException("无法创建独立的接收文件夹");
                    created=candidate;Map<String,Uri> directories=new HashMap<>();directories.put("",created);long total=0;
                    for(ZipEntry entry:entries){
                        PayloadArchive.check(cancelled);String path=entry.getName();if(path.endsWith("/"))path=path.substring(0,path.length()-1);String[] parts=path.split("/");String relative="";Uri directory=created;
                        for(int i=0;i<parts.length-(entry.isDirectory()?0:1);i++){
                            String next=relative.isEmpty()?parts[i]:relative+"/"+parts[i];Uri found=directories.get(next);
                            if(found==null){found=DocumentsContract.createDocument(resolver,directory,DocumentsContract.Document.MIME_TYPE_DIR,parts[i]);if(found==null)throw new IOException("文件夹无法创建");directories.put(next,found);}directory=found;relative=next;
                        }
                        if(entry.isDirectory())continue;
                        String filename=parts[parts.length-1];int dot=filename.lastIndexOf('.');String type=dot<0?null:MimeTypeMap.getSingleton().getMimeTypeFromExtension(filename.substring(dot+1).toLowerCase(Locale.ROOT));
                        Uri target=DocumentsContract.createDocument(resolver,directory,type==null?"application/octet-stream":type,filename);if(target==null)throw new IOException("文件无法创建");
                        try(InputStream in=zip.getInputStream(entry);OutputStream out=resolver.openOutputStream(target,"w")){if(out==null)throw new IOException("文件无法写入");PayloadArchive.copy(in,out,entry.getSize(),cancelled);}total+=entry.getSize();
                    }
                    PayloadArchive.check(cancelled);String actual=AndroidPayloads.displayName(resolver,created);part.delete();
                    return new PeerEngine.Item(actual,created.toString(),DocumentsContract.Document.MIME_TYPE_DIR,total,"folder");
                }
            }
            public void abort(){part.delete();if(created!=null)try{DocumentsContract.deleteDocument(resolver,created);}catch(Exception ignored){}}
        };
    }
}
