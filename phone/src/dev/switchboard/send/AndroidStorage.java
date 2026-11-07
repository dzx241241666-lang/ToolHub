package dev.switchboard.send;

import android.content.*;
import android.net.Uri;
import android.os.Environment;
import android.provider.MediaStore;
import android.webkit.MimeTypeMap;
import java.io.*;
import java.util.Locale;

final class AndroidStorage implements PeerEngine.Storage {
    private final ContentResolver resolver;
    AndroidStorage(Context context) { resolver=context.getContentResolver(); }
    @Override public PeerEngine.Sink create(String name,String mime,long size)throws Exception {
        String inferred=null;int dot=name.lastIndexOf('.');if(dot>=0)inferred=MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(dot+1).toLowerCase(Locale.ROOT));
        final String type=(mime.isEmpty()||mime.equals("application/octet-stream"))&&inferred!=null?inferred:mime.isEmpty()?"application/octet-stream":mime;
        ContentValues values=new ContentValues();
        values.put(MediaStore.Downloads.DISPLAY_NAME,name);
        values.put(MediaStore.Downloads.MIME_TYPE,type);
        values.put(MediaStore.Downloads.RELATIVE_PATH,Environment.DIRECTORY_DOWNLOADS+"/ToolHub Send/");
        values.put(MediaStore.Downloads.IS_PENDING,1);
        Uri uri=resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI,values);
        if(uri==null)throw new IOException("接收目录不可用");
        return new PeerEngine.Sink(){
            public OutputStream stream()throws Exception{OutputStream out=resolver.openOutputStream(uri,"w");if(out==null)throw new IOException("文件无法写入");return out;}
            public PeerEngine.Item commit()throws Exception{ContentValues done=new ContentValues();done.put(MediaStore.Downloads.IS_PENDING,0);if(resolver.update(uri,done,null,null)!=1)throw new IOException("文件保存失败");return new PeerEngine.Item(name,uri.toString(),type,size);}
            public void abort(){try{resolver.delete(uri,null,null);}catch(Exception ignored){}}
        };
    }
}
